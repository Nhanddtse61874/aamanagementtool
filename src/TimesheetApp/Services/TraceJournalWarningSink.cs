using System.Diagnostics;

namespace TimesheetApp.Services;

/// <summary>Default <see cref="IJournalWarningSink"/>: writes the warning to the diagnostic
/// trace as a Warning event. Non-fatal and System.Windows-free so it is safe to inject into
/// services. The warning is recorded, never silently dropped (XC-09).</summary>
public sealed class TraceJournalWarningSink : IJournalWarningSink
{
    public void Warn(string message) => Trace.TraceWarning(message);
}
