namespace TimesheetApp.Services;

// P11 (EX-02/03/05/06): orchestrates the structured per-team export, mirrored to the configured roots.
// For each configured root (ExportRoot1/2, skip empty) × each team (incl. inactive, EX-02) it writes
//   {root}/{Team}/timesheet/{yyyyMM}_timesheet.md
//   {root}/{Team}/daily/{yyyyMMdd}_daily.md
//   {root}/{Team}/tasklist/{yyyyMM}_tasklist.md     (no-data period => no file)
// plus one whole-DB copy at {root}/db (EX-05). Best-effort per root (one failing root doesn't abort the
// other). Reuses the per-team markdown builders + BackupService (no logic duplication).
public interface IExportHubService
{
    // Manual "Export now": regenerates the current + recent completed periods for every team into every
    // configured root. Returns a human-readable status string. (EX-06)
    Task<string> ExportNowAsync();

    // Startup backfill: completed weeks/months strictly before now that have data but no structured file
    // yet (idempotent — skips when the file already exists). Best-effort. (EX-06)
    Task BackfillAsync();
}
