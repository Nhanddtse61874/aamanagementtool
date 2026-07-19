using System.Net;
using System.Net.Sockets;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>M10 Blocker 2 / P2 (<c>.planning/M10-BLOCKERS.md</c>): the OFFLINE restore path. Deleting
/// WPF's Settings tab deletes the only working restore UI in the product — <c>IBackupService.RestoreAsync</c>
/// had zero production callers outside it. This is the replacement: a command-line branch on the API
/// executable, run against a STOPPED API. Deliberately NOT an in-app admin route — restoring while the app
/// runs (open pooled connections, live readers) is the exact hazard this whole design exists to avoid; see
/// <c>SettingsEndpoints.cs</c>'s own comment on why <c>RestoreAsync</c> was never exposed there.
///
/// <para><b>What this earns and what it does not.</b> In-process quiescence by construction: this runs
/// BEFORE <c>WebApplication.Build()</c>, so no Kestrel, no DI container, no pooled connections exist in
/// THIS process while it restores. Cross-process quiescence — that no OTHER copy of the API is running
/// against the same database — is earned only by operator discipline: the runbook's "stop the API and
/// confirm the window is gone" step, backed up (not replaced) by the advisory port probe below.</para>
///
/// <para><b>Six amendments an adversarial review of the first draft of this idea required</b> (M10-BLOCKERS.md,
/// Blocker 2, "Recommendation"):</para>
/// <list type="number">
/// <item>A hard precondition — the SOURCE backup must pass <see cref="SqliteOnlineBackup.IsIntact"/> before
/// anything destructive runs. Ships inside <c>BackupService.RestoreAsync</c> itself (commit 0c739f9), so any
/// future caller inherits it. This class adds the other half: the RESULT must pass the same check too, with
/// a non-zero exit on failure — see <see cref="RunAsync"/>.</item>
/// <item>Called from <c>Program.cs</c> AFTER the startup banner, never before: <c>JsonAppConfig</c> resolves
/// per-Windows-user paths, so running blind can restore into the WRONG database and report success. The
/// banner is the one diagnostic that reveals it. And this REFUSES to proceed when the live .db does not
/// exist rather than creating one — "wrong path" is exactly the case that otherwise prints nothing
/// revealing (<c>BackupService.RestoreAsync</c> skips its own pre-restore safety copy when the db is
/// absent).</item>
/// <item>No <c>BEGIN EXCLUSIVE</c> guard — under WAL it is defined to behave like <c>BEGIN IMMEDIATE</c>: it
/// takes the write lock without excluding readers, so an idle API between requests probes as "clear" while
/// fully running. A port-bind probe on 5080 instead (this single-process, fixed-port deployment always
/// binds it while running — see <c>launchSettings.json</c> / <c>deploy-local.bat</c> / <c>start-web.bat</c>),
/// presented to the operator as ADVISORY: a strong hint, never proof.</item>
/// <item>No claim of "quiescence by construction" for the cross-process case, here or in the runbook — see
/// the class summary above.</item>
/// </list>
///
/// <para>(The remaining two amendments — deciding where <c>WalBackupSafetyTests</c> / <c>BackupServiceTests</c>
/// land, and writing the runbook — are process/documentation items, not code in this class.)</para></summary>
public static class RestoreCli
{
    /// <summary>The port this deployment always binds while running — hardcoded in <c>launchSettings.json</c>,
    /// <c>deploy-local.bat</c> and <c>start-web.bat</c> alike, never configurable per-install today.</summary>
    public const int DefaultProbePort = 5080;

    /// <summary>True when <paramref name="args"/> select the restore branch instead of the normal web host.
    /// Checked in <c>Program.cs</c> BEFORE <c>WebApplication.Build()</c> — but AFTER the startup banner.</summary>
    public static bool IsRestoreCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "--restore", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the offline restore end to end and returns the process exit code. Zero ONLY when the
    /// restore ran and the restored database then passed its own <c>PRAGMA integrity_check</c> — every
    /// other path (bad usage, missing live db, a port that looks occupied, a thrown
    /// <see cref="BackupService.RestoreAsync"/>, or a post-restore integrity failure) returns non-zero.
    /// A restore CLI that fails silently with exit 0 is worse than no CLI at all.</summary>
    public static async Task<int> RunAsync(
        string[] args, IAppConfig config, TextWriter output, int probePort = DefaultProbePort)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            output.WriteLine("Usage: TimesheetApp.Api.exe --restore <path-to-backup.db>");
            return 1;
        }

        var backupPath = args[1];
        var dbPath = config.DbPath;

        // HOLE 11 (M10-BLOCKERS.md): the resolved DbPath was already echoed by Program.cs's startup banner,
        // BEFORE this method ran. If that path is wrong (wrong Windows account, wrong/missing config), this
        // is the operator's one chance to notice before anything destructive happens. RestoreAsync itself
        // would happily "restore" onto a brand-new empty database and never say a word about it.
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            output.WriteLine($"ERROR: no database found at '{dbPath}'.");
            output.WriteLine("Refusing to proceed: --restore never CREATES a database. A missing file at");
            output.WriteLine("the path above almost always means the resolved path is wrong (wrong Windows");
            output.WriteLine("account, wrong config file) -- not that the database is genuinely new. Check");
            output.WriteLine("the 'Database' line printed above, fix the config, then re-run.");
            return 1;
        }

        // HOLE 10: a port-bind probe, not BEGIN EXCLUSIVE -- WAL makes the latter blind to a running,
        // currently-idle server. ADVISORY either way: a bound port is a strong signal (this deployment has
        // nothing else on 5080), but a free port is a hint the API is stopped, never a guarantee -- the
        // runbook's manual "close the window and confirm it's gone" step is what this backs up, not replaces.
        if (IsPortInUse(probePort))
        {
            output.WriteLine($"ERROR: something is already listening on port {probePort}.");
            output.WriteLine("That looks like the API is still running. Stop it -- close the console window");
            output.WriteLine("and confirm it is gone -- then re-run. (Heuristic: catches the common case, but");
            output.WriteLine("is not a proof of safety on its own -- verify manually too.)");
            return 1;
        }
        output.WriteLine($"Port {probePort} is free (advisory only -- also confirm the API window is closed).");

        output.WriteLine($"Restoring '{backupPath}' over '{dbPath}'...");
        try
        {
            await new BackupService(config, new SystemClock()).RestoreAsync(backupPath);
        }
        catch (Exception ex)
        {
            output.WriteLine($"ERROR: restore failed: {ex.Message}");
            return 1;
        }

        // Amendment 1's other half. BackupService.RestoreAsync already refuses an unintact SOURCE before it
        // destroys anything (IsIntact(backupPath), commit 0c739f9). Nothing before this line had verified
        // the RESULT. A restore that silently hands back a corrupt live database is worse than no restore
        // tool at all, so this is loud on purpose.
        if (!SqliteOnlineBackup.IsIntact(dbPath))
        {
            output.WriteLine($"ERROR: restore completed but '{dbPath}' failed its post-restore integrity");
            output.WriteLine("check. Do NOT start the API against it. The pre-restore safety copy this tool");
            output.WriteLine("just made (*.pre-restore_*.bak next to the database) is the way back.");
            return 1;
        }

        output.WriteLine("Restore complete and verified (PRAGMA integrity_check = ok).");
        output.WriteLine("You may start the API normally now.");
        return 0;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false; // bound it ourselves -> nothing else was listening
        }
        catch (SocketException)
        {
            return true; // could not bind -> something already holds the port
        }
    }
}
