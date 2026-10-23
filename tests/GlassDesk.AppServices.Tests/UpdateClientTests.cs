using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GlassDesk.AppServices.Updates;

namespace GlassDesk.AppServices.Tests;

public sealed class UpdateClientTests
{
    private const string Repository = "everlosttime/glassdesk-updates";
    private static readonly Uri Source = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly Uri Mirror = new($"https://mirror.example/repos/{Repository}/releases/latest");

    private static string Fixture(string version = "0.2.10", string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", long size = 4, bool includeSums = false)
    {
        var fileName = $"GlassDesk-{version}-win-x64-Setup.exe";
        var sums = includeSums
            ? ",{\"name\":\"SHA256SUMS.txt\",\"state\":\"uploaded\",\"size\":128,\"browser_download_url\":\"https://github.com/"
              + Repository + "/releases/download/v" + version + "/SHA256SUMS.txt\"}"
            : string.Empty;
        return "{\"tag_name\":\"v" + version + "\",\"draft\":false,\"prerelease\":false,\"body\":\"更新说明\","
            + "\"html_url\":\"https://github.com/" + Repository + "/releases/tag/v" + version + "\","
            + "\"assets\":[{\"name\":\"" + fileName + "\",\"state\":\"uploaded\",\"size\":" + size
            + ",\"digest\":\"" + digest + "\",\"browser_download_url\":\"https://github.com/" + Repository
            + "/releases/download/v" + version + "/" + fileName + "\"}" + sums + "]}";
    }

    private const string NoDigest = "\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"";
    private const string MissingDigest = "\"digest\":null";

    [Fact]
    public void Selects_numeric_newer_version_and_matching_verified_asset()
    {
        var release = UpdateClient.ParseRelease(Fixture(), new Version(0, 2, 9), Source);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 10), release.Version);
        Assert.Equal("更新说明", release.Notes);
        Assert.Equal("GlassDesk-0.2.10-win-x64-Setup.exe", release.FileName);
        Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Empty(release.ChecksumUrl);
    }

    [Theory]
    [InlineData("0.2.8")]
    [InlineData("0.2.9")]
    public void Never_offers_equal_or_older_versions(string version) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture(version), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("\"prerelease\":false", "\"prerelease\":true")]
    [InlineData("\"draft\":false", "\"draft\":true")]
    public void Ignores_unpublished_and_preview_releases(string before, string after) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture().Replace(before, after), new Version(0, 2, 9), Source));

    /// <summary>
    /// A tag the updater cannot offer is "no update", not an error: the check stays successful
    /// and the user simply keeps the installed build.
    /// </summary>
    [Theory]
    [InlineData("0.2.10-preview")]
    [InlineData("0.2.10.1")]
    [InlineData("0.02.10")]
    [InlineData("999999999999.0.0")]
    public void Treats_ambiguous_or_unsupported_versions_as_no_update(string version) =>
        Assert.Null(UpdateClient.ParseRelease(Fixture(version), new Version(0, 2, 9), Source));

    [Theory]
    [InlineData("https://github.com/everlosttime/glassdesk-updates/releases/download/", "https://evil.example/releases/download/")]
    [InlineData("https://github.com/everlosttime/glassdesk-updates/releases/download/", "http://github.com/everlosttime/glassdesk-updates/releases/download/")]
    [InlineData("\"state\":\"uploaded\"", "\"state\":\"new\"")]
    [InlineData("\"size\":4", "\"size\":0")]
    [InlineData("\"size\":4", "\"size\":536870913")]
    [InlineData("-win-x64-Setup.exe", "-win-arm64-Setup.exe")]
    public void Rejects_untrusted_or_incomplete_installers(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(Fixture().Replace(before, after), new Version(0, 2, 9), Source));

    [Fact]
    public void Rejects_a_release_without_any_checksum_information() =>
        Assert.Throws<InvalidDataException>(() =>
            UpdateClient.ParseRelease(Fixture().Replace(NoDigest, MissingDigest), new Version(0, 2, 9), Source));

    [Fact]
    public void Accepts_a_checksum_manifest_when_the_asset_digest_is_missing()
    {
        var release = UpdateClient.ParseRelease(Fixture(includeSums: true).Replace(NoDigest, MissingDigest), new Version(0, 2, 9), Source);
        Assert.NotNull(release);
        Assert.Empty(release.Sha256);
        Assert.Equal($"https://github.com/{Repository}/releases/download/v0.2.10/SHA256SUMS.txt", release.ChecksumUrl);
    }

    [Fact]
    public void Rejects_a_checksum_manifest_that_lives_outside_the_release()
    {
        var tampered = Fixture(includeSums: true)
            .Replace(NoDigest, MissingDigest)
            .Replace("https://github.com/everlosttime/glassdesk-updates/releases/download/v0.2.10/SHA256SUMS.txt", "https://evil.example/SHA256SUMS.txt");
        Assert.Throws<InvalidDataException>(() => UpdateClient.ParseRelease(tampered, new Version(0, 2, 9), Source));
    }

    [Fact]
    public void Accepts_an_api_compatible_mirror_for_the_same_repository() =>
        Assert.Equal(new Version(0, 2, 10), UpdateClient.ParseRelease(Fixture(), new Version(0, 2, 9), Mirror)?.Version);

    [Fact]
    public void Refuses_sources_that_describe_different_repositories()
    {
        var other = new Uri("https://mirror.example/repos/someone/else/releases/latest");
        Assert.Throws<InvalidDataException>(() => new UpdateClient(new HttpClient(), new[] { Source, other }, cacheDirectory: null));
    }

    [Fact]
    public async Task Download_writes_exact_bytes_and_reports_completion_only_after_validation()
    {
        var bytes = Encoding.UTF8.GetBytes("a real installer fixture");
        var release = Release(bytes);
        using var client = new HttpClient(new FixtureHandler(bytes));
        var updater = new UpdateClient(client, Source);
        var root = TempRoot();
        var percentages = new List<int>();
        try
        {
            var file = await updater.DownloadAsync(release, root, new InlineProgress(percentages), CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
            Assert.EndsWith(release.FileName, file);
            Assert.Equal(100, percentages[^1]);
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_verifies_against_the_published_checksum_manifest()
    {
        var bytes = Encoding.UTF8.GetBytes("installer verified by a checksum manifest");
        var sums = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "  GlassDesk-0.2.10-win-x64-Setup.exe\n";
        var release = Release(bytes) with { Sha256 = string.Empty, ChecksumUrl = $"https://github.com/{Repository}/releases/download/v0.2.10/SHA256SUMS.txt" };
        using var http = new HttpClient(new RoutingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? Content(Encoding.UTF8.GetBytes(sums))
            : Content(bytes)));
        var root = TempRoot();
        try
        {
            var file = await new UpdateClient(http, Source).DownloadAsync(release, root, null, CancellationToken.None);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_rejects_an_installer_that_disagrees_with_its_checksum_manifest()
    {
        var bytes = Encoding.UTF8.GetBytes("installer that does not match");
        var sums = new string('c', 64) + "  GlassDesk-0.2.10-win-x64-Setup.exe\n";
        var release = Release(bytes) with { Sha256 = string.Empty, ChecksumUrl = $"https://github.com/{Repository}/releases/download/v0.2.10/SHA256SUMS.txt" };
        using var http = new HttpClient(new RoutingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? Content(Encoding.UTF8.GetBytes(sums))
            : Content(bytes)));
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateClient(http, Source).DownloadAsync(release, root, null, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("truncate")]
    [InlineData("oversize")]
    public async Task Corrupt_download_never_leaves_an_installable_or_partial_file(string mode)
    {
        var expected = Encoding.UTF8.GetBytes("expected installer");
        var actual = mode switch { "tamper" => Encoding.UTF8.GetBytes("modified installer"), "truncate" => expected[..3], _ => new byte[expected.Length + 1] };
        using var http = new HttpClient(new FixtureHandler(actual, omitLength: true));
        var updater = new UpdateClient(http, Source);
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(Release(expected), root, null, CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cancellation_removes_the_partial_download()
    {
        var bytes = new byte[200000];
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FixtureHandler(bytes));
        var root = TempRoot();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateClient(http, Source).DownloadAsync(
                Release(bytes), root, new CancelProgress(cancellation), cancellation.Token));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Prune_removes_only_installers_older_than_a_day()
    {
        var root = TempRoot();
        try
        {
            var stale = Directory.CreateDirectory(Path.Combine(root, "stale"));
            File.WriteAllText(Path.Combine(stale.FullName, "old.exe"), "old");
            Directory.SetLastWriteTimeUtc(stale.FullName, DateTime.UtcNow.AddDays(-3));
            var fresh = Directory.CreateDirectory(Path.Combine(root, "fresh"));
            File.WriteAllText(Path.Combine(fresh.FullName, "recent.exe"), "recent");

            UpdateClient.PruneCache(root, fresh.FullName);

            Assert.False(Directory.Exists(stale.FullName));
            Assert.True(Directory.Exists(fresh.FullName));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Metadata_http_failure_is_not_reported_as_latest_version()
    {
        using var http = new HttpClient(new FixtureHandler([], HttpStatusCode.NotFound));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(http, Source).CheckAsync(new Version(0, 2, 9), CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.Contains("update-source.json", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limited_metadata_request_explains_the_retry()
    {
        using var http = new HttpClient(new FixtureHandler([], HttpStatusCode.Forbidden));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(http, Source).CheckAsync(new Version(0, 2, 9), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Contains("频率", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_next_configured_source()
    {
        var requests = 0;
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            requests++;
            return request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Content(Encoding.UTF8.GetBytes(Fixture()));
        }));
        var release = await new UpdateClient(http, new[] { Source, Mirror }, cacheDirectory: null)
            .CheckAsync(new Version(0, 2, 9), CancellationToken.None);
        Assert.Equal(new Version(0, 2, 10), release?.Version);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Every_source_failing_surfaces_the_last_failure()
    {
        using var http = new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new UpdateClient(http, new[] { Source, Mirror }, cacheDirectory: null).CheckAsync(new Version(0, 2, 9), CancellationToken.None));
    }

    [Fact]
    public async Task Conditional_request_reuses_the_cached_metadata()
    {
        var cache = TempRoot();
        var sendConditional = false;
        var count = 0;
        using var http = new HttpClient(new RoutingHandler(request =>
        {
            count++;
            if (request.Headers.IfNoneMatch.Count > 0)
            {
                sendConditional = true;
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            var response = Content(Encoding.UTF8.GetBytes(Fixture()));
            response.Headers.ETag = new EntityTagHeaderValue("\"release-1\"");
            return response;
        }));
        try
        {
            var updater = new UpdateClient(http, new[] { Source }, cache);
            Assert.Equal(new Version(0, 2, 10), (await updater.CheckAsync(new Version(0, 2, 9), CancellationToken.None))?.Version);
            Assert.Equal(new Version(0, 2, 10), (await updater.CheckAsync(new Version(0, 2, 9), CancellationToken.None))?.Version);
            Assert.True(sendConditional);
            Assert.Equal(2, count);
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task Offline_check_answers_from_the_cached_metadata()
    {
        var cache = TempRoot();
        try
        {
            using (var online = new HttpClient(new RoutingHandler(request =>
            {
                var response = Content(Encoding.UTF8.GetBytes(Fixture()));
                response.Headers.ETag = new EntityTagHeaderValue("\"release-1\"");
                return response;
            })))
            {
                await new UpdateClient(online, new[] { Source }, cache).CheckAsync(new Version(0, 2, 9), CancellationToken.None);
            }

            using var offline = new HttpClient(new RoutingHandler(_ => throw new HttpRequestException("network is down")));
            var release = await new UpdateClient(offline, new[] { Source }, cache).CheckAsync(new Version(0, 2, 9), CancellationToken.None);
            Assert.Equal(new Version(0, 2, 10), release?.Version);
        }
        finally { Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task Offline_check_without_a_cache_still_fails()
    {
        using var http = new HttpClient(new RoutingHandler(_ => throw new HttpRequestException("network is down")));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new UpdateClient(http, new[] { Source }, cacheDirectory: null).CheckAsync(new Version(0, 2, 9), CancellationToken.None));
    }

    private static UpdateRelease Release(byte[] bytes) => UpdateClient.ParseRelease(
        Fixture(digest: "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), size: bytes.Length), new Version(0, 2, 9), Source)!;
    private static string TempRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "GlassDesk-update-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    private static HttpResponseMessage Content(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class InlineProgress(List<int> values) : IProgress<int> { public void Report(int value) => values.Add(value); }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<int> { public void Report(int value) { if (value > 0) source.Cancel(); } }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.UserAgent.Any());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixtureHandler(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK, bool omitLength = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Headers.UserAgent.Any());
            return Task.FromResult(new HttpResponseMessage(status) { Content = omitLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes) });
        }
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
