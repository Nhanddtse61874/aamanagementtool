using TimesheetApp.Models;

namespace TimesheetApp.Tests;

/// <summary>Terse hours-only <see cref="WeekRow"/> builder for fixtures that predate the per-cell
/// row_version (M8.4) and assert on hours alone — the WPF-ViewModel tests, which drive grid/total/edit
/// behaviour and never look at a version.
///
/// <para>This deliberately lives in the TEST project rather than as a convenience constructor on
/// <see cref="WeekRow"/> itself, for two independent reasons:</para>
/// <list type="number">
/// <item><b>It would break the wire.</b> <c>WeekRow</c> is the contract for <c>GET /api/timesheet/week</c>
/// (serialised straight onto the response — there is no week DTO). System.Text.Json can only deserialize a
/// type with a SINGULAR parameterized constructor; a second overload makes it throw
/// <c>NotSupportedException</c> on every week read. That is not hypothetical — it is what adding one did.</item>
/// <item><b>It is a version-dropping constructor on a type whose whole job is now to carry versions</b> —
/// precisely the silent drop M8.4 exists to fix. Production code must be unable to build a versionless
/// WeekRow by accident.</item>
/// </list>
/// <para>Versions here are null, which is honest: these fixtures have no database and no versions to state.</para></summary>
internal static class WeekRows
{
    public static WeekRow Row(
        int taskId, string backlogCode, string taskName, int orderIndex,
        decimal? mon, decimal? tue, decimal? wed, decimal? thu, decimal? fri) =>
        new(taskId, backlogCode, taskName, orderIndex,
            new WeekCell(mon, null), new WeekCell(tue, null), new WeekCell(wed, null),
            new WeekCell(thu, null), new WeekCell(fri, null));
}
