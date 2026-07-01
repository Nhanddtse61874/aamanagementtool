namespace TimesheetApp.Services;

// P14 (SP-01): severity of a destination check, drives the status-line color in Settings.
public enum DestinationLevel { Ok, Warning, Error }

// P14 (SP-01): the outcome of verifying an export/SharePoint destination folder.
public sealed record DestinationVerifyResult(DestinationLevel Level, string Message);

// P14 (SP-01): verifies that a configured "Shared/SharePoint folder" is a usable write destination.
// File-sync approach: the path is a mapped SharePoint drive / WebDAV UNC / synced folder — writing there
// uploads to SharePoint. The write-probe is authoritative; the SharePoint-ness is a warning-level heuristic.
public interface ISharePointDestinationValidator
{
    DestinationVerifyResult Verify(string? path);
}
