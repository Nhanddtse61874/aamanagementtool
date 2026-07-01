using System.IO;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P14 (SP-01): the SharePoint/export destination verifier. Classify is pure (string only); Verify runs a
// real write-probe over a throwaway temp dir (mirrors the repo tests' real-filesystem style).
public sealed class SharePointDestinationValidatorTests : IDisposable
{
    private readonly SharePointDestinationValidator _sut = new();
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "worklog-sp-verify-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ---- Classify (pure) ----

    [Theory]
    [InlineData("http://x.sharepoint.com/sites/a")]
    [InlineData("https://contoso.sharepoint.com/sites/Team/Shared%20Documents")]
    public void Classify_web_url_is_WebUrl(string path) =>
        Assert.Equal(SharePointDestinationValidator.PathKind.WebUrl,
            SharePointDestinationValidator.Classify(path));

    [Theory]
    [InlineData(@"\\host\share\Worklog")]                                   // UNC file share
    [InlineData(@"\\contoso.sharepoint.com@SSL\DavWWWRoot\sites\T\Docs")]   // SharePoint WebDAV UNC
    [InlineData(@"C:\Users\Nhan\OneDrive - Contoso\Worklog")]              // OneDrive-synced segment
    [InlineData(@"D:\team.sharepoint.com backup")]                         // "sharepoint" anywhere
    public void Classify_network_or_synced_is_SharePointOrNetwork(string path) =>
        Assert.Equal(SharePointDestinationValidator.PathKind.SharePointOrNetwork,
            SharePointDestinationValidator.Classify(path));

    [Theory]
    [InlineData(@"C:\Temp\Worklog")]
    [InlineData(@"D:\Exports\logs")]
    public void Classify_plain_local_is_PlainLocal(string path) =>
        Assert.Equal(SharePointDestinationValidator.PathKind.PlainLocal,
            SharePointDestinationValidator.Classify(path));

    // ---- Verify (write-probe) ----

    [Fact] // A web URL is rejected with guidance, before any file I/O.
    public void Verify_web_url_is_Error()
    {
        var r = _sut.Verify("https://contoso.sharepoint.com/sites/Team");
        Assert.Equal(DestinationLevel.Error, r.Level);
        Assert.Contains("web URL", r.Message);
    }

    [Fact] // Empty / whitespace path is an error, not a crash.
    public void Verify_empty_is_Error()
    {
        Assert.Equal(DestinationLevel.Error, _sut.Verify("").Level);
        Assert.Equal(DestinationLevel.Error, _sut.Verify("   ").Level);
        Assert.Equal(DestinationLevel.Error, _sut.Verify(null).Level);
    }

    [Fact] // A writable plain-local folder warns (writable, but won't reach SharePoint) — and leaves no probe file.
    public void Verify_writable_local_folder_is_Warning_and_cleans_up()
    {
        var r = _sut.Verify(_tempRoot);

        Assert.Equal(DestinationLevel.Warning, r.Level);
        Assert.True(Directory.Exists(_tempRoot));                 // created by the probe
        Assert.Empty(Directory.GetFiles(_tempRoot));              // probe file removed after the check
    }

    [Fact] // A writable folder whose path looks like SharePoint/OneDrive verifies OK.
    public void Verify_writable_sharepoint_looking_folder_is_Ok()
    {
        // A real, writable dir that also matches the "OneDrive" heuristic → OK (not just Warning).
        var synced = Path.Combine(_tempRoot, "OneDrive - Contoso", "Worklog");
        var r = _sut.Verify(synced);

        Assert.Equal(DestinationLevel.Ok, r.Level);
        Assert.True(Directory.Exists(synced));
    }

    [Fact] // An un-createable path (invalid characters) is a write error, not an exception.
    public void Verify_invalid_path_is_Error()
    {
        var bad = _tempRoot + "\\inva|id<>dir";   // '|','<','>' are invalid on Windows
        var r = _sut.Verify(bad);
        Assert.Equal(DestinationLevel.Error, r.Level);
        Assert.Contains("write", r.Message, StringComparison.OrdinalIgnoreCase);
    }
}
