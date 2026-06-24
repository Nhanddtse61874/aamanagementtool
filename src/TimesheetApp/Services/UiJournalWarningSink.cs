namespace TimesheetApp.Services;

/// <summary>UI-facing <see cref="IJournalWarningSink"/> (XC-09). Wraps an inner sink (so the warning
/// is still traced and never swallowed), records the latest message, and raises
/// <see cref="WarningRaised"/> so the shell can surface it as a banner. Stays System.Windows-free:
/// the View layer (App) marshals the event onto the UI thread.</summary>
public sealed class UiJournalWarningSink : IJournalWarningSink
{
    private readonly IJournalWarningSink _inner;

    public UiJournalWarningSink(IJournalWarningSink inner) => _inner = inner;

    /// <summary>The most recent warning message (empty until one is raised).</summary>
    public string LatestWarning { get; private set; } = string.Empty;

    /// <summary>Raised on each warning; subscribers read <see cref="LatestWarning"/>.</summary>
    public event EventHandler? WarningRaised;

    public void Warn(string message)
    {
        _inner.Warn(message);                 // keep observability — still traced (XC-09)
        LatestWarning = message;
        WarningRaised?.Invoke(this, EventArgs.Empty);
    }
}
