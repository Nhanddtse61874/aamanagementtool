namespace TimesheetApp.Services;

/// <summary>
/// M8.2 (spec §9) — the <see cref="IJournalWarningSink"/> the API host registers. Drops the warning.
///
/// The XC-09 check behind this sink asks <see cref="TimesheetApp.Data.SqliteMaintenance.IsJournalGone"/>
/// whether a <c>&lt;db&gt;-journal</c> file lingers after a commit, which under journal_mode=DELETE means
/// a transaction was interrupted. Under WAL that file is never created at all: the check would pass
/// forever, and a sink that reported it would be reporting nothing.
///
/// So this is not "silence a warning we do not want to hear" — the WAL profile makes the signal itself
/// meaningless, and a permanently-clean check is worse than no check because it reads as evidence. The
/// server's equivalent signal is WAL checkpointing, which belongs to the connection profile, not here.
/// A DI swap: no service constructor changes.
/// </summary>
public sealed class NoOpJournalWarningSink : IJournalWarningSink
{
    public void Warn(string message) { }
}
