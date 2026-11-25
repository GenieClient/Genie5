using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Update.Sources;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Update downloads are HTTPS from a GitHub host, at every redirect hop (dev #46).
///
/// <para>The download URL comes out of the feed's JSON, and feeds are
/// user-addable, so a hostile or compromised feed picks it. These tests drive
/// the real sources against a fake network and assert on what was REQUESTED:
/// a refused URL must never be contacted at all, since for an internal address
/// the request itself is the damage.</para>
/// </summary>
public class UpdateDownloadUrlGuardTests
{
    /// <summary>A fake network: canned replies by exact URL, and a record of
    /// every URL asked for. Does not follow redirects, like the client the
    /// sources build for themselves.</summary>
    private sealed class FakeNet : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _replies = new();
        public List<string> Requested { get; } = new();

        public FakeNet Json(string url, string json)
        {
            _replies[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            return this;
        }

        public FakeNet Bytes(string url, string body)
        {
            _replies[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
            return this;
        }

        public FakeNet Redirect(string url, string location)
        {
            _replies[url] = () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return r;
            };
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var response = _replies.TryGetValue(url, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private const string Listing = "https://api.github.com/repos/Owner/Repo/contents/";

    private static string ContentsJson(string downloadUrl) =>
        "[{\"name\":\"Map1.xml\",\"type\":\"file\",\"sha\":\"abc\",\"size\":3," +
        "\"download_url\":\"" + downloadUrl + "\"}]";

    private static string ReleaseJson(string assetUrl) =>
        "{\"tag_name\":\"v1.0.0\",\"published_at\":\"2026-01-01T00:00:00Z\",\"assets\":[" +
        "{\"name\":\"Plugin.dll\",\"size\":3,\"browser_download_url\":\"" + assetUrl + "\"}]}";

    // ── the allow-list itself ────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/Owner/Repo/releases/download/v1/Plugin.dll")]
    [InlineData("https://api.github.com/repos/Owner/Repo/contents/")]
    [InlineData("https://raw.githubusercontent.com/Owner/Repo/HEAD/Map1.xml")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://RAW.GitHubUserContent.com/Owner/Repo/HEAD/Map1.xml")]
    public void GitHub_https_urls_are_allowed(string url)
        => Assert.True(GithubHttp.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("http://raw.githubusercontent.com/Owner/Repo/HEAD/Map1.xml")]   // not HTTPS
    [InlineData("https://evil.example/Map1.xml")]                               // not GitHub
    [InlineData("https://169.254.169.254/latest/meta-data/")]                   // cloud metadata
    [InlineData("https://127.0.0.1/admin")]                                     // loopback
    [InlineData("https://localhost/admin")]
    [InlineData("https://github.com.evil.example/x")]                           // look-alike suffix
    [InlineData("https://evilgithubusercontent.com/x")]                         // no dot boundary
    [InlineData("https://githubusercontent.com.evil.example/x")]
    [InlineData("https://github.com@evil.example/x")]                           // userinfo trick
    [InlineData("https://github.com:8443/x")]                                   // odd port
    [InlineData("https://gist.github.com/x")]                                   // not a host we fetch from
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://github.com/x")]
    public void Everything_else_is_refused(string url)
        => Assert.False(GithubHttp.IsAllowed(new Uri(url)));

    // ── contents source (maps, loose files) ──────────────────────────────────

    [Theory]
    [InlineData("http://raw.githubusercontent.com/Owner/Repo/HEAD/Map1.xml")]
    [InlineData("https://evil.example/Map1.xml")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("not a url")]
    public async Task A_listing_cannot_send_the_download_elsewhere(string downloadUrl)
    {
        var net    = new FakeNet().Json(Listing, ContentsJson(downloadUrl));
        var source = new GithubContentsSource("Owner", "Repo", http: new HttpClient(net));

        var list = await source.GetFileListAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => source.DownloadFileAsync(list.Files[0]));

        Assert.Equal(new[] { Listing }, net.Requested);   // the bad URL was never contacted
    }

    [Fact]
    public async Task A_normal_contents_download_still_works()
    {
        const string raw = "https://raw.githubusercontent.com/Owner/Repo/HEAD/Map1.xml";
        var net    = new FakeNet().Json(Listing, ContentsJson(raw)).Bytes(raw, "<zone/>");
        var source = new GithubContentsSource("Owner", "Repo", http: new HttpClient(net));

        var list  = await source.GetFileListAsync();
        var bytes = await source.DownloadFileAsync(list.Files[0]);

        Assert.Equal("<zone/>", Encoding.UTF8.GetString(bytes));
    }

    // ── releases source (plugins) ────────────────────────────────────────────

    private const string Latest = "https://api.github.com/repos/Owner/Repo/releases/latest";
    private const string Asset  = "https://github.com/Owner/Repo/releases/download/v1.0.0/Plugin.dll";
    private const string Cdn    = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x";

    [Fact]
    public async Task A_release_asset_follows_GitHubs_own_redirect()
    {
        var net = new FakeNet()
            .Json(Latest, ReleaseJson(Asset))
            .Redirect(Asset, Cdn)
            .Bytes(Cdn, "dll");
        var source = new GithubReleasesSource("Owner", "Repo", new HttpClient(net));

        var release = await source.GetLatestReleaseAsync();
        var bytes   = await source.DownloadAssetAsync(release!.Assets[0]);

        Assert.Equal("dll", Encoding.UTF8.GetString(bytes));
        Assert.Equal(new[] { Latest, Asset, Cdn }, net.Requested);
    }

    [Theory]
    [InlineData("https://evil.example/Plugin.dll")]
    [InlineData("http://release-assets.githubusercontent.com/Plugin.dll")]
    [InlineData("https://10.0.0.5/internal")]
    public async Task A_redirect_off_GitHub_is_refused_without_being_followed(string target)
    {
        var net = new FakeNet()
            .Json(Latest, ReleaseJson(Asset))
            .Redirect(Asset, target)
            .Bytes(target, "payload");
        var source = new GithubReleasesSource("Owner", "Repo", new HttpClient(net));

        var release = await source.GetLatestReleaseAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => source.DownloadAssetAsync(release!.Assets[0]));

        Assert.DoesNotContain(target, net.Requested);
    }

    [Fact]
    public async Task A_second_hop_is_checked_as_strictly_as_the_first()
    {
        const string evil = "https://evil.example/Plugin.dll";
        var net = new FakeNet()
            .Json(Latest, ReleaseJson(Asset))
            .Redirect(Asset, Cdn)
            .Redirect(Cdn, evil)
            .Bytes(evil, "payload");
        var source = new GithubReleasesSource("Owner", "Repo", new HttpClient(net));

        var release = await source.GetLatestReleaseAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => source.DownloadAssetAsync(release!.Assets[0]));

        Assert.DoesNotContain(evil, net.Requested);
    }

    [Fact]
    public async Task An_asset_url_off_GitHub_is_never_contacted()
    {
        const string evil = "https://evil.example/Plugin.dll";
        var net    = new FakeNet().Json(Latest, ReleaseJson(evil)).Bytes(evil, "payload");
        var source = new GithubReleasesSource("Owner", "Repo", new HttpClient(net));

        var release = await source.GetLatestReleaseAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => source.DownloadAssetAsync(release!.Assets[0]));

        Assert.Equal(new[] { Latest }, net.Requested);
    }

    [Fact]
    public async Task A_redirect_loop_ends()
    {
        var net = new FakeNet()
            .Json(Latest, ReleaseJson(Asset))
            .Redirect(Asset, Asset);
        var source = new GithubReleasesSource("Owner", "Repo", new HttpClient(net));

        var release = await source.GetLatestReleaseAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => source.DownloadAssetAsync(release!.Assets[0]));

        Assert.True(net.Requested.Count(u => u == Asset) <= 6);
    }

    [Fact]
    public async Task A_renamed_repo_listing_redirect_within_the_api_host_is_followed()
    {
        const string moved = "https://api.github.com/repositories/12345/contents/";
        var net = new FakeNet()
            .Redirect(Listing, moved)
            .Json(moved, ContentsJson("https://raw.githubusercontent.com/Owner/Repo/HEAD/Map1.xml"));
        var source = new GithubContentsSource("Owner", "Repo", http: new HttpClient(net));

        var list = await source.GetFileListAsync();

        Assert.Single(list.Files);
    }

    // ── a client that follows redirects on its own ───────────────────────────

    /// <summary>Stands in for a caller-supplied client with auto-redirect on:
    /// the hops are invisible and the response reports where it landed.</summary>
    private sealed class AutoFollowingNet : HttpMessageHandler
    {
        private readonly string _landsOn;
        public AutoFollowingNet(string landsOn) => _landsOn = landsOn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content        = new ByteArrayContent(Encoding.UTF8.GetBytes("payload")),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, _landsOn),
            });
    }

    [Fact]
    public async Task Bytes_from_wherever_an_auto_following_client_landed_are_refused()
    {
        var http = new HttpClient(new AutoFollowingNet("https://evil.example/Plugin.dll"));

        await Assert.ThrowsAsync<HttpRequestException>(() => GithubHttp.GetBytesAsync(http, Asset));
    }
}
