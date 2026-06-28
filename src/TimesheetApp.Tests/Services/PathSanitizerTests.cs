using System.IO;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P11 (EX-07): the shared path-segment sanitizer used by the structured export.
public class PathSanitizerTests
{
    private readonly IPathSanitizer _sut = new PathSanitizer();

    [Fact]
    public void Normal_Name_Is_Unchanged()
    {
        Assert.Equal("Architect Improvement", _sut.SanitizeSegment("Architect Improvement"));
    }

    [Fact]
    public void Invalid_Filename_Chars_Are_Replaced()
    {
        // '/', '\', ':', '*', '?' etc. are invalid on Windows; '|' is the canonical pipe case.
        var result = _sut.SanitizeSegment("Team A/B:C|D");
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('|', result);
        foreach (var bad in Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(bad, result);
    }

    [Fact]
    public void Trailing_Dots_And_Spaces_Are_Trimmed()
    {
        Assert.Equal("Team", _sut.SanitizeSegment("Team. . "));
        Assert.Equal("Team", _sut.SanitizeSegment("  Team  "));
    }

    [Fact]
    public void Empty_Falls_Back_To_TeamId_When_Id_Positive()
    {
        Assert.Equal("team-7", _sut.SanitizeSegment("", fallbackId: 7));
        Assert.Equal("team-7", _sut.SanitizeSegment("   ", fallbackId: 7));
    }

    [Fact]
    public void Empty_Falls_Back_To_Unnamed_When_No_Id()
    {
        Assert.Equal("unnamed", _sut.SanitizeSegment(""));
        Assert.Equal("unnamed", _sut.SanitizeSegment("   ", fallbackId: 0));
    }

    [Fact]
    public void All_Invalid_Chars_Collapse_To_Underscores_Not_Fallback()
    {
        // A name made only of invalid chars sanitizes to underscores (non-empty) — not the fallback.
        var result = _sut.SanitizeSegment("///", fallbackId: 3);
        Assert.Equal("___", result);
    }
}
