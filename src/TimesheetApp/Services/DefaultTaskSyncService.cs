using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Maintains the hidden DEFAULT backlog (one per team, TM-04) and reconciles the GLOBAL
/// DefaultTasks into Tasks under each active team's DEFAULT. Rename = soft-delete old + insert new
/// (TimeLogs preserved, decision 7). (DATA-03, SET-04, XC-10)</summary>
public sealed class DefaultTaskSyncService : IDefaultTaskSyncService
{
    private const string DefaultCode = "DEFAULT";

    private readonly IBacklogRepository _requests;
    private readonly ITaskRepository _tasks;
    private readonly IDefaultTaskRepository _defaults;
    private readonly ITeamRepository _teams;
    private readonly IDbBackupHelper _backup;
    private readonly IAppConfig _config;
    private readonly IJournalWarningSink _journalWarnings;

    public DefaultTaskSyncService(
        IBacklogRepository requests, ITaskRepository tasks,
        IDefaultTaskRepository defaults, ITeamRepository teams, IDbBackupHelper backup,
        IAppConfig config, IJournalWarningSink journalWarnings)
    {
        _requests = requests;
        _tasks = tasks;
        _defaults = defaults;
        _teams = teams;
        _backup = backup;
        _config = config;
        _journalWarnings = journalWarnings;
    }

    public async Task<int> EnsureDefaultBacklogIdAsync(int teamId)
    {
        // P10 (R2): DEFAULT is unique per team — resolve per team, NEVER the global GetByCodeAsync.
        var existing = await _requests.GetDefaultForTeamAsync(teamId);
        if (existing is not null) return existing.Id;
        return await _requests.InsertAsync(
            new Backlog(0, DefaultCode, DefaultCode, DateTimeOffset.UtcNow, TeamId: teamId));
    }

    public async Task SyncAsync()
    {
        await _backup.BackupAsync(); // XC-10: sync is a bulk write -> backup first (no-op when DB absent).

        // Global DefaultTasks list materialized once per active team's DEFAULT backlog (TM-04).
        var activeDefaults = (await _defaults.GetActiveAsync())
            .Select(d => d.TaskName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var team in await _teams.GetActiveAsync())
            await ReconcileTeamDefaultAsync(team.Id, activeDefaults);

        // XC-09: this is a bulk write path -> after the writes the rollback journal must be gone.
        // A lingering "<db>-journal" means a transaction was interrupted; surface it, never swallow.
        if (!SqliteMaintenance.IsJournalGone(_config.DbPath))
            _journalWarnings.Warn(
                $"A SQLite rollback journal persists next to '{_config.DbPath}' after syncing default tasks. " +
                "The last bulk write may have been interrupted; verify the database integrity.");
    }

    // Insert-missing / soft-delete-orphan reconcile, scoped to ONE team's DEFAULT backlog.
    private async Task ReconcileTeamDefaultAsync(int teamId, IReadOnlySet<string> activeDefaults)
    {
        var defReqId = await EnsureDefaultBacklogIdAsync(teamId);

        var tasksUnderDefault = await _tasks.GetActiveByBacklogAsync(defReqId);
        var existingNames = tasksUnderDefault
            .Select(t => t.TaskName)
            .ToHashSet(StringComparer.Ordinal);

        // (a) active DefaultTask with no active Task under this team's DEFAULT -> insert (new + rename-new).
        var order = 0;
        foreach (var name in activeDefaults)
        {
            if (!existingNames.Contains(name))
                await _tasks.InsertAsync(new TaskItem(0, defReqId, name, order, true));
            order++;
        }

        // (b) active Task under this team's DEFAULT with no active DefaultTask -> soft-delete
        //     (covers hide + rename-old; preserves TimeLogs, XC-06).
        foreach (var task in tasksUnderDefault)
            if (!activeDefaults.Contains(task.TaskName))
                await _tasks.SetActiveAsync(task.Id, false);
    }
}
