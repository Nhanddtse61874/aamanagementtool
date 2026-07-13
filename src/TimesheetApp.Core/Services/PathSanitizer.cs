using System.IO;
using System.Text;

namespace TimesheetApp.Services;

// P11 (EX-07): pure path-segment sanitizer. Registered as a singleton (W3). No I/O, no state.
public sealed class PathSanitizer : IPathSanitizer
{
    private const char Replacement = '_';

    public string SanitizeSegment(string name, int fallbackId = 0)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name?.Length ?? 0);
        foreach (var ch in name ?? string.Empty)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? Replacement : ch);

        // Windows forbids trailing dots/spaces on a path segment.
        var result = sb.ToString().Trim().TrimEnd('.', ' ').Trim();

        if (string.IsNullOrWhiteSpace(result))
            return fallbackId > 0 ? $"team-{fallbackId}" : "unnamed";

        return result;
    }
}
