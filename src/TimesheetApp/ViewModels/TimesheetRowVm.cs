using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TimesheetApp.ViewModels;

/// One bindable Mon-Fri timesheet row. null day = empty cell = 0h (distinct from 0).
/// Per-cell red validation via INotifyDataErrorInfo; the 8h *column* total lives on the owner VM.
public sealed class TimesheetRowVm : ObservableObject, INotifyDataErrorInfo
{
    public int TaskId { get; init; }
    public string BacklogCode { get; init; } = "";
    public string Project { get; init; } = "";
    public string TaskName { get; init; } = "";

    private decimal? _mon, _tue, _wed, _thu, _fri;
    public decimal? Mon { get => _mon; set => SetDay(ref _mon, value, nameof(Mon), DayColumn.Mon); }
    public decimal? Tue { get => _tue; set => SetDay(ref _tue, value, nameof(Tue), DayColumn.Tue); }
    public decimal? Wed { get => _wed; set => SetDay(ref _wed, value, nameof(Wed), DayColumn.Wed); }
    public decimal? Thu { get => _thu; set => SetDay(ref _thu, value, nameof(Thu), DayColumn.Thu); }
    public decimal? Fri { get => _fri; set => SetDay(ref _fri, value, nameof(Fri), DayColumn.Fri); }

    public decimal RowTotal => (_mon ?? 0) + (_tue ?? 0) + (_wed ?? 0) + (_thu ?? 0) + (_fri ?? 0);

    /// Raised after a day value commits, carrying the row + which column changed; the owner VM
    /// recomputes column totals, re-gates Save, and auto-saves just that cell.
    public event Action<TimesheetRowVm, DayColumn>? DayChanged;

    private readonly Dictionary<string, string> _errors = new();

    private void SetDay(ref decimal? field, decimal? value, string propName, DayColumn col)
    {
        var rounded = Round1(value);
        Validate(propName, value);                  // validate the RAW entry (catches >1-decimal)
        if (SetProperty(ref field, rounded, propName))
        {
            OnPropertyChanged(nameof(RowTotal));
            DayChanged?.Invoke(this, col);
        }
    }

    /// True when the given day cell currently holds a validation error (e.g. >8h, ≤0, >1 decimal) —
    /// the owner VM checks this before auto-persisting so a red cell is never written to storage.
    public bool HasErrorFor(DayColumn col) => _errors.ContainsKey(col.ToString());

    private void Validate(string propName, decimal? raw)
    {
        string? error = null;
        if (raw is { } v)
        {
            if (v <= 0) error = "Hours must be greater than 0.";
            else if (v > 8m) error = "A single cell cannot exceed 8h.";
            else if (decimal.Round(v, 1, MidpointRounding.AwayFromZero) != v)
                error = "At most 1 decimal place.";
        }
        if (error is null) _errors.Remove(propName);
        else _errors[propName] = error;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propName));
    }

    private static decimal? Round1(decimal? v) =>
        v is null ? null : decimal.Round(v.Value, 1, MidpointRounding.AwayFromZero);

    // --- INotifyDataErrorInfo ---
    public bool HasErrors => _errors.Count > 0;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var e)
            ? new[] { e } : Array.Empty<string>();
}
