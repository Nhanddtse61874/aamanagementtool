namespace TimesheetApp.Services;

internal static class DateHelpers
{
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
