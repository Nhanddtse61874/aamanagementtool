namespace TimesheetApp.Services;

// Weekly markdown archive of standup data (DR-09). One file per week under
// <db-folder>/StandupArchives/{yyyyMMdd}_daily.md (stamp = the week's Monday).
public interface IStandupArchiveService
{
    // "{yyyyMMdd}_daily.md" for the Monday of the week containing anyDayInWeek.
    string FileNameFor(DateOnly anyDayInWeek);

    // P11 (EX-04): builds the week's markdown scoped to teamIds (null => all teams). Returns null when
    // the scoped range has no entries (no-data => no file). Content only — the hub supplies the path.
    Task<string?> BuildWeekMarkdownAsync(IReadOnlyList<int>? teamIds, DateOnly weekMonday);

    // Writes the Mon–Fri markdown for that week; returns the full path, or null when the week has
    // no entries (no empty file written). Overwrites an existing file (idempotent).
    Task<string?> ExportWeekAsync(DateOnly anyDayInWeek);

    // Startup hook: for every completed week (before the current week) that has standup data but no
    // archive file yet, generate it.
    Task BackfillMissingWeeksAsync();
}
