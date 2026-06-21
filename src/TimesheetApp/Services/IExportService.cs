using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Headless export contract (architecture spec §4). Returns byte[]/string only —
// NEVER opens SaveFileDialog (dialog ownership is a View concern). EXP-01..04.
public interface IExportService
{
    Task<byte[]> ExportExcelAsync(ExportFilter filter);      // ClosedXML → MemoryStream → ToArray (EXP-01)
    Task<string> ExportMarkdownAsync(ExportFilter filter);   // StringBuilder (EXP-02/03/04)
}
