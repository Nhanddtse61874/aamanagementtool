namespace TimesheetApp.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TimesheetApp.Models;

// Working-set for creating/editing a single custom Tag (TAG-01), shown in the Settings overlay.
// Mirrors TemplateEditorViewModel: ForCreate/ForEdit factories + observable fields. Icon is a free-text
// emoji / Segoe glyph (A4) with a curated quick-pick row; Color is a hex string with preset swatches and
// a live preview chip (rendered via HexToBrushConverter in the view).
public sealed partial class TagEditorViewModel : ObservableObject
{
    private TagEditorViewModel() { }

    public bool IsEditMode { get; private init; }

    // The tag id being edited (0 in create mode).
    public int TagId { get; private init; }

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string _icon = string.Empty;
    [ObservableProperty] private string _color = "#0F766E";   // teal default (theme Accent)

    // A4: a small curated emoji/glyph quick-pick row.
    public IReadOnlyList<string> IconQuickPicks { get; } =
        new[] { "\U0001F525", "⭐", "\U0001F41B", "⚠️", "\U0001F680", "\U0001F4CC", "✅", "\U0001F50D" };

    // A4: predefined color swatches.
    public IReadOnlyList<string> ColorSwatches { get; } =
        new[] { "#0F766E", "#2563EB", "#7C3AED", "#DB2777", "#DC2626", "#D97706", "#16A34A", "#64748B" };

    public static TagEditorViewModel ForCreate() => new() { IsEditMode = false };

    public static TagEditorViewModel ForEdit(Tag tag) => new()
    {
        IsEditMode = true,
        TagId = tag.Id,
        Text = tag.Text,
        Icon = tag.Icon,
        Color = tag.Color,
    };
}
