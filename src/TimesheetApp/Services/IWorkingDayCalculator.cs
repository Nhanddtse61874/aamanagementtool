namespace TimesheetApp.Services;

// Pure working-day math (HOL-02). A day is non-working iff Saturday/Sunday OR present in the holiday set.
// No DB / no IClock inside → deterministic. Callers load the holiday set once per reload and pass it in (A2).
// One shared helper backs smart-input, schedule math, the ≤2-day window, and the Gantt axis (§6.1).
public interface IWorkingDayCalculator
{
    bool IsWorkingDay(DateOnly d, IReadOnlySet<DateOnly> holidays);                                  // false for Sat/Sun/holiday
    IReadOnlyList<DateOnly> WorkingDaysBetween(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);
    int CountWorkingDays(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);               // inclusive both ends; from>to => 0
}
