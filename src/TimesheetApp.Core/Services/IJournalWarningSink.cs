namespace TimesheetApp.Services;

/// <summary>Observability seam for XC-09. After a bulk write, services check
/// <see cref="TimesheetApp.Data.SqliteMaintenance.IsJournalGone"/>; a lingering rollback
/// journal means a transaction was interrupted and the .db may be at risk. The warning is
/// routed here instead of being swallowed, and instead of pulling System.Windows.* into a
/// service. The View/App layer supplies a concrete sink (e.g. a status banner or log).</summary>
public interface IJournalWarningSink
{
    /// <summary>Surface a non-fatal data-integrity warning to the user/operator.</summary>
    void Warn(string message);
}
