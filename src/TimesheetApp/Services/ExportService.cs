using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

public sealed class ExportService : IExportService
{
    private const string DefaultCode = "DEFAULT";
    private const string NoTeamLabel = "(no team)";   // v8: rows whose backlog has no team yet (pre-bootstrap)

    private readonly ITimeLogRepository _logs;
    private readonly IUserRepository _users;
    private readonly IBacklogRepository _requests;

    public ExportService(
        ITimeLogRepository logs,
        IUserRepository users,
        IBacklogRepository requests)
    {
        _logs = logs;
        _users = users;
        _requests = requests;
    }

    public async Task<string> ExportMarkdownAsync(ExportFilter filter)
    {
        var rows = await LoadAsync(filter);
        var sb = new StringBuilder();
        sb.Append("# Timesheet — ")
          .Append(filter.Year.ToString("D4", CultureInfo.InvariantCulture))
          .Append('/')
          .Append(filter.Month.ToString("D2", CultureInfo.InvariantCulture))
          .AppendLine().AppendLine();

        // team -> user -> "group label" -> rows. (TM-08: team grouping above user.)
        // Real backlog group label = "{code} — {project}".
        // DEFAULT group label = "{code} — {task_name}" (EXP-03, decision 1).
        foreach (var teamGroup in rows
                     .GroupBy(r => r.TeamName ?? NoTeamLabel)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("## ").Append(teamGroup.Key).AppendLine().AppendLine();

            foreach (var userGroup in teamGroup
                         .GroupBy(r => (r.UserId, r.UserName))
                         .OrderBy(g => g.Key.UserName, StringComparer.Ordinal))
            {
                sb.Append("### ").Append(userGroup.Key.UserName).AppendLine().AppendLine();

                var groups = userGroup
                    .GroupBy(r => GroupKey(r))
                    .OrderBy(g => g.Key.Sort, StringComparer.Ordinal);

                foreach (var grp in groups)
                {
                    sb.Append("#### ").Append(grp.Key.Header).AppendLine();
                    sb.AppendLine("| Date | Task | Hours |");
                    sb.AppendLine("| --- | --- | --- |");
                    foreach (var row in grp.OrderBy(r => r.WorkDate).ThenBy(r => r.TaskId))
                    {
                        sb.Append("| ")
                          .Append(row.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                          .Append(" | ")
                          .Append(EscapePipe(row.TaskName))
                          .Append(" | ")
                          .Append(FormatHours(row.Hours))
                          .AppendLine(" |");
                    }
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }

    public async Task<byte[]> ExportExcelAsync(ExportFilter filter)
    {
        var rows = await LoadAsync(filter);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Timesheet");

        ws.Cell(1, 1).Value = "Team";          // TM-08: leftmost Team column
        ws.Cell(1, 2).Value = "User";
        ws.Cell(1, 3).Value = "Backlog";
        ws.Cell(1, 4).Value = "Project";
        ws.Cell(1, 5).Value = "Task";
        ws.Cell(1, 6).Value = "Date";
        ws.Cell(1, 7).Value = "Hours";

        var r = 2;
        foreach (var row in rows
                     .OrderBy(x => x.TeamName ?? NoTeamLabel, StringComparer.Ordinal)
                     .ThenBy(x => x.UserName, StringComparer.Ordinal)
                     .ThenBy(x => x.WorkDate)
                     .ThenBy(x => x.BacklogCode, StringComparer.Ordinal)
                     .ThenBy(x => x.TaskId))
        {
            ws.Cell(r, 1).Value = row.TeamName ?? NoTeamLabel;
            ws.Cell(r, 2).Value = row.UserName;
            ws.Cell(r, 3).Value = row.BacklogCode;
            ws.Cell(r, 4).Value = row.Project;
            ws.Cell(r, 5).Value = row.TaskName;
            ws.Cell(r, 6).Value = row.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ws.Cell(r, 7).Value = (double)row.Hours;
            r++;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // --- helpers ---

    private async Task<IReadOnlyList<TimeLogReportRow>> LoadAsync(ExportFilter filter)
    {
        var from = new DateOnly(filter.Year, filter.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var rows = await _logs.GetExportRowsAsync(from, to, filter.Project, filter.TeamIds);
        if (filter.UserId is int uid)
            rows = rows.Where(x => x.UserId == uid).ToList();
        return rows;
    }

    private static (string Sort, string Header) GroupKey(TimeLogReportRow r) =>
        r.BacklogCode == DefaultCode
            ? ($"{r.BacklogCode}|{r.TaskName}", $"{r.BacklogCode} — {r.TaskName}")   // EXP-03
            : ($"{r.BacklogCode}|{r.Project}", $"{r.BacklogCode} — {r.Project}");   // EXP-02

    // "4" not "4.0"; "3.5" stays "3.5" (EXP-04).
    internal static string FormatHours(decimal h) =>
        h == Math.Truncate(h)
            ? ((long)h).ToString(CultureInfo.InvariantCulture)
            : h.ToString("0.#", CultureInfo.InvariantCulture);

    // Escape table-breaking pipe in task names (EXP-04).
    internal static string EscapePipe(string s) => s.Replace("|", @"\|");
}
