namespace TimesheetApp.Services;

// Monthly markdown archive of the Task List (TL-09). One file per month under
// <archive-dir>/{yyyyMM}_tasklist.md. Mirrors IStandupArchiveService: no data => no file, idempotent
// overwrite, startup backfill of completed months.
public interface ITaskListArchiveService
{
    // "{yyyyMM}_tasklist.md" for the given month.
    string FileNameFor(int year, int month);

    // Writes the month's markdown overview; returns the full path, or null when the month has no data
    // (no current members and no moved-out members → no file written). Overwrites idempotently.
    Task<string?> ExportMonthAsync(int year, int month);

    // Startup hook: for every month strictly before the current month that has data but no archive
    // file yet, generate it.
    Task BackfillMissingMonthsAsync();
}
