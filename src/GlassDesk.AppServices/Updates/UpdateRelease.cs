namespace GlassDesk.AppServices.Updates;

/// <summary>
/// A release the updater is willing to install. <paramref name="Sha256"/> is empty when the
/// release published <c>SHA256SUMS.txt</c> instead of a per-asset digest; in that case
/// <paramref name="ChecksumUrl"/> points at the manifest verified before downloading.
/// </summary>
public sealed record UpdateRelease(
    Version Version,
    string FileName,
    Uri DownloadUrl,
    string Sha256,
    long Size,
    string Notes,
    Uri ReleasePage,
    string ChecksumUrl = "");
