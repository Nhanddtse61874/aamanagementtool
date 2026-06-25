namespace TimesheetApp.Services;

// Weekly markdown archive of standup data (DR-09). One file per week under
// <db-folder>/StandupArchives/{yyyyMMdd}_daily.md (stamp = the week's Monday).
public interface IStandupArchiveService
{
    // "{yyyyMMdd}_daily.md" for the Monday of the week containing anyDayInWeek.
    string FileNameFor(DateOnly anyDayInWeek);

    // Writes the Mon–Fri markdown for that week; returns the full path, or null when the week has
    // no entries (no empty file written). Overwrites an existing file (idempotent).
    Task<string?> ExportWeekAsync(DateOnly anyDayInWeek);

    // Startup hook: for every completed week (before the current week) that has standup data but no
    // archive file yet, generate it.
    Task BackfillMissingWeeksAsync();
}
