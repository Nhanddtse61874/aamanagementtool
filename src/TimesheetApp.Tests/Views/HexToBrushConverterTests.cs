using System.Globalization;
using System.Windows.Media;
using TimesheetApp.Views.Converters;
using Xunit;

namespace TimesheetApp.Tests.Views;

// HexToBrushConverter must never throw on user-entered hex (TAG-01/02) — valid hex yields the matching
// brush, anything malformed yields a neutral fallback brush rather than crashing the UI thread.
public sealed class HexToBrushConverterTests
{
    private static readonly HexToBrushConverter Conv = new();

    private static SolidColorBrush Convert(object? value) =>
        Assert.IsType<SolidColorBrush>(Conv.Convert(value, typeof(Brush), null, CultureInfo.InvariantCulture));

    [Fact]
    public void ValidHex_ReturnsMatchingBrush()
    {
        var brush = Convert("#0F766E");
        Assert.Equal(Color.FromRgb(0x0F, 0x76, 0x6E), brush.Color);
    }

    [Fact]
    public void GarbageInput_ReturnsFallbackBrush_NotException()
    {
        var brush = Convert("not-a-color");   // would throw inside ColorConverter — must be swallowed
        Assert.NotNull(brush);
    }

    [Fact]
    public void NullOrBlank_ReturnsFallbackBrush()
    {
        Assert.NotNull(Convert(null));
        Assert.NotNull(Convert("   "));
    }

    [Fact]
    public void ReturnedBrush_IsFrozen()
    {
        Assert.True(Convert("#2563EB").IsFrozen);
    }
}
