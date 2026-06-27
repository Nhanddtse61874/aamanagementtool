namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;

// HOL-01: a month-grid calendar (owned by SettingsViewModel) for marking/unmarking holidays.
// Holds the current year/month, prev/next navigation, and a 6x7 grid of day cells. Clicking a day
// in the displayed month toggles a Holiday upsert/delete then refreshes; weekends are flagged via
// IWorkingDayCalculator/DayOfWeek so the view can style them distinctly from holidays.
public sealed partial class HolidayCalendarViewModel : ObservableObject
{
    private readonly IHolidayRepository _holidays;
    private readonly IMessenger _messenger;

    public HolidayCalendarViewModel(IHolidayRepository holidays, IMessenger messenger)
    {
        _holidays = holidays;
        _messenger = messenger;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _year = today.Year;
        _month = today.Month;
    }

    [ObservableProperty] private int _year;
    [ObservableProperty] private int _month;

    // Human label for the month header, e.g. "June 2026".
    public string MonthLabel =>
        new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    // The visible 6-week grid (always 42 cells so the UniformGrid stays stable).
    public ObservableCollection<HolidayDayCell> Days { get; } = new();

    public async Task LoadAsync()
    {
        var marked = (await _holidays.GetForMonthAsync(Year, Month))
            .Select(h => h.Date)
            .ToHashSet();

        Days.Clear();
        var first = new DateOnly(Year, Month, 1);
        // Grid starts on the Monday on/before the 1st (Mon..Sun columns).
        var offset = ((int)first.DayOfWeek + 6) % 7;   // Mon=0 .. Sun=6
        var start = first.AddDays(-offset);

        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            Days.Add(new HolidayDayCell(
                date,
                date.Day,
                isWeekend,
                IsHoliday: marked.Contains(date),
                IsInMonth: date.Month == Month && date.Year == Year));
        }
    }

    [RelayCommand]
    private async Task PrevMonthAsync()
    {
        var d = new DateOnly(Year, Month, 1).AddMonths(-1);
        Year = d.Year; Month = d.Month;
        OnPropertyChanged(nameof(MonthLabel));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextMonthAsync()
    {
        var d = new DateOnly(Year, Month, 1).AddMonths(1);
        Year = d.Year; Month = d.Month;
        OnPropertyChanged(nameof(MonthLabel));
        await LoadAsync();
    }

    // Clicking a day cell toggles its holiday state (out-of-month cells are ignored by the view).
    [RelayCommand]
    private async Task ToggleHolidayAsync(DateOnly date)
    {
        var existing = (await _holidays.GetForMonthAsync(date.Year, date.Month))
            .Any(h => h.Date == date);

        if (existing)
            await _holidays.DeleteAsync(date);
        else
            await _holidays.UpsertAsync(date, null);

        await LoadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Holidays));
    }
}

// One day cell in the holiday calendar grid.
public sealed record HolidayDayCell(
    DateOnly Date, int DayNumber, bool IsWeekend, bool IsHoliday, bool IsInMonth);
