namespace TimesheetApp.Services;

// P11 (EX-07): a shared helper that converts an arbitrary name (e.g. a team name) into a safe
// folder/file path segment for the structured export. Replaces the inline pattern at ReportsViewModel.
public interface IPathSanitizer
{
    /// <summary>
    /// Returns a filesystem-safe path segment for <paramref name="name"/>: every
    /// <see cref="System.IO.Path.GetInvalidFileNameChars"/> char is replaced with '_', and trailing
    /// dots/spaces are trimmed (Windows forbids them on a segment). When the result is empty/whitespace,
    /// falls back to <c>team-{fallbackId}</c> (or <c>unnamed</c> when <paramref name="fallbackId"/> &lt;= 0).
    /// </summary>
    string SanitizeSegment(string name, int fallbackId = 0);
}
