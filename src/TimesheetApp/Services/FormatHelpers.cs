using System.Globalization;

namespace TimesheetApp.Services;

internal static class FormatHelpers
{
    // "4" not "4.0"; "3.5" stays "3.5" (EXP-04).
    public static string FormatHours(decimal h) =>
        h == Math.Truncate(h)
            ? ((long)h).ToString(CultureInfo.InvariantCulture)
            : h.ToString("0.#", CultureInfo.InvariantCulture);

    // Estimate/logged hours render whole numbers without decimals (TL-05/§7).
    public static string FormatHoursNullable(decimal? v)
    {
        if (v is not { } h) return "—";
        return h == Math.Truncate(h)
            ? ((long)h).ToString(CultureInfo.InvariantCulture)
            : h.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // ExportService.ExportMarkdownAsync always emits the "# Timesheet — yyyy/MM" header; a team with no
    // logs that month produces no "| Date | Task | Hours |" table. Use that table header as the has-data marker.
    public static bool HasTimesheetData(string md) =>
        md.Contains("| Date | Task | Hours |", StringComparison.Ordinal);
}
