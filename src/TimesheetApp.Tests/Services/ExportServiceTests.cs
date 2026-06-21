using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Moq;
using TimesheetApp.Models;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class ExportServiceTests
{
    private static TimeLogReportRow Row(
        int userId, string userName, string code, string project,
        int taskId, string taskName, string date, decimal hours) =>
        new(userId, userName, code, project, taskId, taskName,
            DateOnly.Parse(date), hours);

    private static (ExportService svc, Mock<ITimeLogRepository> logs,
                    Mock<IUserRepository> users, Mock<IRequestRepository> reqs)
        Build(IReadOnlyList<TimeLogReportRow> rows)
    {
        var logs = new Mock<ITimeLogRepository>();
        logs.Setup(r => r.GetExportRowsAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<string?>()))
            .ReturnsAsync(rows);
        var users = new Mock<IUserRepository>();
        var reqs = new Mock<IRequestRepository>();
        var svc = new ExportService(logs.Object, users.Object, reqs.Object);
        return (svc, logs, users, reqs);
    }

    // ---------- EXP-02: Markdown structure ----------
    [Fact]
    public async Task Markdown_RealRequest_RendersHeaderAndTable()
    {
        var rows = new[]
        {
            Row(1, "Nguyen Van A", "REQ-001", "ProjectX", 10, "Implement", "2026-06-16", 4m),
            Row(1, "Nguyen Van A", "REQ-001", "ProjectX", 11, "Review",    "2026-06-16", 4m),
            Row(1, "Nguyen Van A", "REQ-001", "ProjectX", 12, "Testing",   "2026-06-17", 3m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains("# Timesheet — 2026/06", md);
        Assert.Contains("## Nguyen Van A", md);
        Assert.Contains("### REQ-001 — ProjectX", md);
        Assert.Contains("| Date | Task | Hours |", md);
        Assert.Contains("| --- | --- | --- |", md);
        Assert.Contains("| 2026-06-16 | Implement | 4 |", md);
        Assert.Contains("| 2026-06-17 | Testing | 3 |", md);
    }

    // ---------- EXP-03: DEFAULT grouped by task name ----------
    [Fact]
    public async Task Markdown_DefaultRequest_HeaderUsesTaskNameNotProject()
    {
        var rows = new[]
        {
            Row(1, "Nguyen Van A", "DEFAULT", "DEFAULT", 90, "Annual Leave", "2026-06-20", 8m),
            Row(1, "Nguyen Van A", "DEFAULT", "DEFAULT", 91, "Meeting",      "2026-06-19", 2m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains("### DEFAULT — Annual Leave", md);
        Assert.Contains("### DEFAULT — Meeting", md);
        Assert.DoesNotContain("### DEFAULT — DEFAULT", md);
        // Annual Leave row sits under its own header
        Assert.Contains("| 2026-06-20 | Annual Leave | 8 |", md);
        Assert.Contains("| 2026-06-19 | Meeting | 2 |", md);
    }

    // ---------- EXP-04: hours formatting + pipe escaping ----------
    [Fact]
    public async Task Markdown_Hours_IntegerWhenWhole_OneDecimalOtherwise()
    {
        var rows = new[]
        {
            Row(1, "U", "REQ-001", "P", 1, "T", "2026-06-16", 4m),    // whole -> "4"
            Row(1, "U", "REQ-001", "P", 1, "T", "2026-06-17", 3.5m),  // frac  -> "3.5"
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains("| 2026-06-16 | T | 4 |", md);
        Assert.Contains("| 2026-06-17 | T | 3.5 |", md);
        Assert.DoesNotContain("4.0", md);
    }

    [Fact]
    public async Task Markdown_TaskNameWithPipe_IsEscaped()
    {
        var rows = new[]
        {
            Row(1, "U", "REQ-001", "P", 1, "A|B", "2026-06-16", 1m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(null, 2026, 6, null));

        Assert.Contains(@"| 2026-06-16 | A\|B | 1 |", md);
    }

    [Fact]
    public async Task Markdown_FiltersByUserId_WhenProvided()
    {
        var rows = new[]
        {
            Row(1, "Alice", "REQ-001", "P", 1, "T", "2026-06-16", 4m),
            Row(2, "Bob",   "REQ-001", "P", 1, "T", "2026-06-16", 4m),
        };
        var (svc, _, _, _) = Build(rows);

        var md = await svc.ExportMarkdownAsync(new ExportFilter(2, 2026, 6, null));

        Assert.Contains("## Bob", md);
        Assert.DoesNotContain("## Alice", md);
    }

    // ---------- EXP-01: Excel reopen ----------
    [Fact]
    public async Task Excel_ProducesReopenableWorkbook_WithHeaderAndRows()
    {
        var rows = new[]
        {
            Row(1, "Alice", "REQ-001", "ProjectX", 10, "Implement", "2026-06-16", 4m),
            Row(1, "Alice", "DEFAULT", "DEFAULT",  90, "Annual Leave", "2026-06-20", 8m),
        };
        var (svc, _, _, _) = Build(rows);

        var bytes = await svc.ExportExcelAsync(new ExportFilter(null, 2026, 6, null));

        Assert.NotEmpty(bytes);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);
        // header row
        Assert.Equal("User", ws.Cell(1, 1).GetString());
        Assert.Equal("Request", ws.Cell(1, 2).GetString());
        Assert.Equal("Project", ws.Cell(1, 3).GetString());
        Assert.Equal("Task", ws.Cell(1, 4).GetString());
        Assert.Equal("Date", ws.Cell(1, 5).GetString());
        Assert.Equal("Hours", ws.Cell(1, 6).GetString());
        // first data row
        Assert.Equal("Alice", ws.Cell(2, 1).GetString());
        Assert.Equal("REQ-001", ws.Cell(2, 2).GetString());
        Assert.Equal("Implement", ws.Cell(2, 4).GetString());
        Assert.Equal("2026-06-16", ws.Cell(2, 5).GetString());
        Assert.Equal(4d, ws.Cell(2, 6).GetDouble());
        // second data row present
        Assert.Equal("Annual Leave", ws.Cell(3, 4).GetString());
        Assert.Equal(8d, ws.Cell(3, 6).GetDouble());
    }

    [Fact]
    public async Task Excel_PassesMonthRangeAndProjectFilterToRepo()
    {
        var (svc, logs, _, _) = Build(Array.Empty<TimeLogReportRow>());

        await svc.ExportExcelAsync(new ExportFilter(null, 2026, 6, "ProjectX"));

        logs.Verify(r => r.GetExportRowsAsync(
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "ProjectX"), Times.Once);
    }
}
