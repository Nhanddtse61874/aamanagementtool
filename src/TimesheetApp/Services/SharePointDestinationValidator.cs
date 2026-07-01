using System.IO;

namespace TimesheetApp.Services;

// P14 (SP-01): see ISharePointDestinationValidator. Registered as a singleton (no state). The write-probe
// is the ground truth ("can we actually put files here?"); Classify + IsNetworkDrive only decide whether a
// writable folder is a real SharePoint/network destination or just a local folder (a warning, not a block).
public sealed class SharePointDestinationValidator : ISharePointDestinationValidator
{
    private const string ProbePrefix = ".worklog-verify-";

    // Pure classification of the raw path string (no I/O) — the write-probe in Verify decides reachability.
    internal enum PathKind { WebUrl, SharePointOrNetwork, PlainLocal }

    public DestinationVerifyResult Verify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(DestinationLevel.Error, "Enter a folder path first.");

        path = path.Trim();

        var kind = Classify(path);
        if (kind == PathKind.WebUrl)
            return new(DestinationLevel.Error,
                "That looks like a web URL. In SharePoint use “Open in Explorer” (or map it as a drive), " +
                "then paste the drive/folder path here — not the https:// address.");

        // Authoritative check: can we actually create the folder and write a file into it?
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ProbePrefix + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return new(DestinationLevel.Error, $"Can’t write to this folder: {ex.Message}");
        }

        // Writable. Is it really a SharePoint/synced/network target, or just a plain local folder?
        if (kind == PathKind.SharePointOrNetwork || IsNetworkDrive(path))
            return new(DestinationLevel.Ok,
                "Verified — writable SharePoint / network destination. Exports will upload here.");

        return new(DestinationLevel.Warning,
            "Writable, but this looks like a local folder — files stay on this PC and will NOT reach " +
            "SharePoint unless it is a mapped SharePoint drive or a synced location.");
    }

    // WebUrl: http(s). SharePointOrNetwork: a UNC path (incl. the SharePoint WebDAV form
    // \\tenant.sharepoint.com@SSL\DavWWWRoot\...), any "sharepoint"/"DavWWWRoot" in the path, or an
    // "OneDrive…" path segment. Everything else is treated as a plain local folder.
    internal static PathKind Classify(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return PathKind.WebUrl;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return PathKind.SharePointOrNetwork;

        if (path.Contains("sharepoint", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("DavWWWRoot", StringComparison.OrdinalIgnoreCase))
            return PathKind.SharePointOrNetwork;

        foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (seg.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
                return PathKind.SharePointOrNetwork;

        return PathKind.PlainLocal;
    }

    // A mapped drive letter backed by a network share (Z:\ over WebDAV/SMB) reads as DriveType.Network.
    // UNC roots throw in DriveInfo, but those are already caught by Classify → SharePointOrNetwork.
    private static bool IsNetworkDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }
}
