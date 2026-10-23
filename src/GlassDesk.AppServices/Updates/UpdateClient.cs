using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassDesk.AppServices.Updates;

/// <summary>Stable GitHub releases and bounded, checksum-verified installer downloads.</summary>
public sealed partial class UpdateClient
{
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    private const int MaximumMetadataSize = 512 * 1024;
    private const int MaximumChecksumSize = 64 * 1024;
    private const string ChecksumAssetName = "SHA256SUMS.txt";
    private const string CacheFileName = "latest-release.json";
    private static readonly char[] ChecksumSeparators = { ' ', '\t', '*' };
    private readonly HttpClient _http;
    private readonly Uri[] _sources;
    private readonly string _repository;
    private readonly string? _cacheDirectory;

    public UpdateClient(HttpClient http, Uri source)
        : this(http, new[] { source }, cacheDirectory: null)
    {
    }

    /// <summary>
    /// Creates a client that queries <paramref name="sources"/> in order and keeps the last
    /// successful answer (plus its ETag) under <paramref name="cacheDirectory"/> when supplied.
    /// A conditional request then answers 304 without spending rate limit, and a dead network
    /// still resolves the last known release instead of failing the check outright.
    /// </summary>
    public UpdateClient(HttpClient http, IReadOnlyList<Uri> sources, string? cacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("至少需要一个更新源。", nameof(sources));
        var repositories = sources.Select(Repository).Distinct(StringComparer.Ordinal).ToArray();
        if (repositories.Length != 1) throw new InvalidDataException("所有更新源必须指向同一个发布仓库。");
        _http = http;
        _sources = sources.ToArray();
        _repository = repositories[0];
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory) ? null : Path.GetFullPath(cacheDirectory);
    }

    /// <summary>Repository identity ("owner/name") every source and download URL must agree on.</summary>
    public string RepositoryName => _repository;

    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(current);
        var cached = TryReadCache();
        Exception? failure = null;
        foreach (var source in _sources)
        {
            try
            {
                var (release, payload, etag) = await QueryAsync(source, current, cached, token).ConfigureAwait(false);
                if (payload is not null) TryWriteCache(payload, etag);
                return release;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                failure = new TimeoutException("检查更新超时，请稍后重试。");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
            {
                failure = ex;
            }
        }
        // Every source is unreachable: answer from the last known metadata instead of failing.
        if (cached is not null) return ParseRelease(cached.Payload, current, _sources[0]);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        throw new InvalidOperationException("没有可用的更新源。");
    }

    public static UpdateRelease? ParseRelease(string json, Version current, Uri source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(current);
        var repository = Repository(source);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var text = tag.StartsWith('v') ? tag[1..] : tag;
            // A tag we cannot offer (preview, four-part, overflow) is "no update", not an error:
            // the check still succeeds and the user simply stays on the installed build.
            if (!StableVersionPattern().IsMatch(text) || !Version.TryParse(text, out var version)) return null;
            // Compare product version, not the intentionally stable assembly identity (0.2.2.0).
            var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (version <= installed) return null;
            var fileName = $"GlassDesk-{version}-win-x64-Setup.exe";
            var assets = root.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == fileName).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("此版本没有唯一的 Windows x64 安装包。");
            var asset = assets[0];
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerSize || asset.GetProperty("state").GetString() != "uploaded")
                throw new InvalidDataException("更新安装包尚未就绪或大小无效。");
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() ?? string.Empty : string.Empty;
            var checksumUrl = string.Empty;
            if (!DigestPattern().IsMatch(digest))
            {
                // Older publication tooling does not emit "digest"; fall back to a checksum
                // manifest published as a sibling asset of the same release.
                var sums = root.GetProperty("assets").EnumerateArray()
                    .Where(entry => entry.GetProperty("name").GetString() == ChecksumAssetName).ToArray();
                var expectedSums = $"https://github.com/{repository}/releases/download/{tag}/{ChecksumAssetName}";
                if (sums.Length != 1
                    || sums[0].GetProperty("state").GetString() != "uploaded"
                    || sums[0].GetProperty("browser_download_url").GetString() != expectedSums)
                    throw new InvalidDataException("更新安装包缺少 SHA-256 校验信息。");
                checksumUrl = expectedSums;
            }
            var url = asset.GetProperty("browser_download_url").GetString();
            var expected = $"https://github.com/{repository}/releases/download/{tag}/{fileName}";
            var page = root.GetProperty("html_url").GetString();
            if (url != expected || page != $"https://github.com/{repository}/releases/tag/{tag}")
                throw new InvalidDataException("更新地址不属于配置的发布仓库。");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            return new UpdateRelease(version, fileName, new Uri(url), digest.Length == 71 ? digest[7..].ToLowerInvariant() : string.Empty,
                size, notes.Length > 12000 ? notes[..12000] : notes, new Uri(page!), checksumUrl);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("更新信息格式无效。", ex);
        }
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string cacheRoot, IProgress<int>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.DownloadUrl.AbsoluteUri != $"https://github.com/{_repository}/releases/download/v{release.Version}/{release.FileName}"
            && release.DownloadUrl.AbsoluteUri != $"https://github.com/{_repository}/releases/download/{release.Version}/{release.FileName}")
            throw new InvalidDataException("更新下载地址无效。");
        ValidateInstaller(release);
        var sha256 = release.Sha256.Length == 0
            ? await ReadChecksumAsync(release, token).ConfigureAwait(false)
            : release.Sha256;
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "installer.partial");
        var final = Path.Combine(directory, release.FileName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var request = Request(release.DownloadUrl, metadata: false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                throw new InvalidDataException("更新下载被重定向到不安全的地址。");
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("安装包大小与更新信息不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var lastProgress = -1;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                progress?.Report(0);
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("安装包超出声明大小。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    var percent = (int)Math.Min(99, received * 100 / release.Size);
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，请重试下载。");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, final);
            progress?.Report(100);
            PruneCache(cacheRoot, directory);
            return final;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void ValidateInstaller(UpdateRelease release)
    {
        if (!StableVersionPattern().IsMatch(release.Version.ToString())
            || release.FileName != $"GlassDesk-{release.Version}-win-x64-Setup.exe"
            || (!DigestPattern().IsMatch("sha256:" + release.Sha256) && release.ChecksumUrl.Length == 0)
            || release.Size <= 0 || release.Size > MaximumInstallerSize)
            throw new InvalidDataException("安装包信息无效。");
    }

    /// <summary>Removes installers left behind by earlier downloads so the cache cannot grow forever.</summary>
    internal static void PruneCache(string cacheRoot, string keepDirectory)
    {
        try
        {
            var keep = Path.GetFullPath(keepDirectory);
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var directory in Directory.EnumerateDirectories(Path.GetFullPath(cacheRoot)))
            {
                if (string.Equals(Path.GetFullPath(directory), keep, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.GetLastWriteTimeUtc(directory) > cutoff) continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private async Task<(UpdateRelease? Release, string? Payload, string? ETag)> QueryAsync(
        Uri source, Version current, CacheEntry? cached, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(source, metadata: true);
        if (cached is { ETag: { Length: > 0 } previous } && EntityTagHeaderValue.TryParse(previous, out var entityTag))
            request.Headers.IfNoneMatch.Add(entityTag);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            // 304 means the cached metadata is still current: no rate limit spent, nothing to rewrite.
            if (cached is null) throw new InvalidDataException("更新源没有返回可用的更新信息。");
            return (ParseRelease(cached.Payload, current, source), null, null);
        }
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaximumMetadataSize)
            throw new InvalidDataException("更新信息过大。");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumMetadataSize) throw new InvalidDataException("更新信息过大。");
            output.Write(buffer, 0, read);
        }
        var payload = Encoding.UTF8.GetString(output.ToArray());
        return (ParseRelease(payload, current, source), payload, response.Headers.ETag?.ToString());
    }

    private async Task<string> ReadChecksumAsync(UpdateRelease release, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var request = Request(new Uri(release.ChecksumUrl), metadata: false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength > MaximumChecksumSize)
            throw new InvalidDataException("更新校验文件过大。");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        if (text.Length > MaximumChecksumSize) throw new InvalidDataException("更新校验文件过大。");
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(ChecksumSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[1] != release.FileName) continue;
            if (!HexPattern().IsMatch(parts[0])) throw new InvalidDataException("更新校验文件格式无效。");
            return parts[0].ToLowerInvariant();
        }
        throw new InvalidDataException("更新校验文件中找不到安装包。");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                "更新源拒绝了请求（可能已超过访问频率限制），请稍后重试。",
            HttpStatusCode.NotFound => "更新源不存在，请检查 update-source.json 中的地址。",
            _ when (int)response.StatusCode >= 500 => "更新服务器暂时不可用，请稍后重试。",
            _ => string.Create(CultureInfo.InvariantCulture, $"更新源返回了意外的状态码 {(int)response.StatusCode}。")
        };
        throw new HttpRequestException(message, inner: null, response.StatusCode);
    }

    private CacheEntry? TryReadCache()
    {
        if (_cacheDirectory is null) return null;
        try
        {
            var file = Path.Combine(_cacheDirectory, CacheFileName);
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > MaximumMetadataSize * 3L) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var payload = document.RootElement.GetProperty("Payload").GetString();
            if (string.IsNullOrEmpty(payload)) return null;
            var etag = document.RootElement.TryGetProperty("ETag", out var value) ? value.GetString() : null;
            return new CacheEntry(payload, etag);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private void TryWriteCache(string payload, string? etag)
    {
        if (_cacheDirectory is null || payload.Length > MaximumMetadataSize) return;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var file = Path.Combine(_cacheDirectory, CacheFileName);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CacheRecord(etag, payload)));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only cache only costs an extra request next time; never fail the check for it.
        }
    }

    private static HttpRequestMessage Request(Uri url, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("GlassDesk-Updater/1.0");
        if (metadata) request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    /// <summary>
    /// Derives "owner/name" from a releases endpoint. The host is deliberately not pinned to
    /// api.github.com so an API-compatible mirror can be configured; the download and release
    /// page URLs are still required to be the canonical github.com paths for that repository.
    /// </summary>
    private static string Repository(Uri source)
    {
        if (!source.IsAbsoluteUri || source.Scheme != "https" || source.UserInfo.Length != 0
            || source.Query.Length != 0 || source.Fragment.Length != 0
            || !SourcePattern().IsMatch(source.AbsolutePath))
            throw new InvalidDataException("更新源必须是 HTTPS 的 GitHub Releases 接口。");
        var segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', segments.Skip(1).Take(2));
    }

    private sealed record CacheEntry(string Payload, string? ETag);

    private sealed record CacheRecord(string? ETag, string Payload);

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();
    [GeneratedRegex(@"^sha256:[0-9a-fA-F]{64}$")]
    private static partial Regex DigestPattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex HexPattern();
    [GeneratedRegex(@"^/repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/latest$")]
    private static partial Regex SourcePattern();
}
