using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Genie.Core.Update.Sources;

/// <summary>
/// The one HTTP path the GitHub-backed update sources fetch through (dev #46).
///
/// <para>Download URLs come out of a feed's JSON (<c>download_url</c>,
/// <c>browser_download_url</c>), and feeds are user-addable, so a hostile or
/// compromised feed chooses them. Fetching them as given let a feed point the
/// client at plain HTTP or at any host, including ones only reachable from the
/// user's own network. Every request made here must be HTTPS to a GitHub host,
/// and so must every redirect hop on the way to the bytes.</para>
///
/// <para>Redirects are followed by hand for that reason: the stock handler
/// follows them before the caller can look at where they lead. GitHub's own
/// hops stay inside the allow-list (release assets redirect from
/// <c>github.com</c> to <c>release-assets.githubusercontent.com</c>; a renamed
/// repository redirects within <c>api.github.com</c>).</para>
/// </summary>
internal static class GithubHttp
{
    private const string DefaultUserAgent = "Genie5-Updater";

    /// <summary>GitHub's own chains are one or two hops; this only bounds a loop.</summary>
    private const int MaxRedirects = 5;

    /// <summary>A client for an update source to own: no automatic redirects
    /// (so each hop passes through <see cref="GetAsync"/>), the GitHub v3
    /// Accept header, and a long timeout — listings are small, but a source
    /// downloads every file in series on the same client.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        return http;
    }

    /// <summary>True for an HTTPS URL, on the default port, whose host is
    /// GitHub's: <c>github.com</c>, <c>api.github.com</c>, or anything under
    /// <c>githubusercontent.com</c> (raw files and release assets).</summary>
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;

        var host = uri.IdnHost;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GET <paramref name="url"/>, following redirects only while
    /// each hop is an allowed GitHub HTTPS URL. A refused URL throws
    /// <see cref="HttpRequestException"/> BEFORE any request is sent to it, so
    /// callers' existing network-failure handling covers it. The caller
    /// disposes the response and checks its status.</summary>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient http, string url, CancellationToken ct = default)
    {
        Uri.TryCreate(url, UriKind.Absolute, out var current);

        for (var hop = 0; ; hop++)
        {
            if (!IsAllowed(current))
                throw new HttpRequestException(
                    $"Refused to fetch '{Describe(current, url)}': update downloads must be HTTPS from a GitHub host.");

            var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);

            // A caller-supplied client may still follow redirects itself; the
            // hops were then invisible, so at least refuse where it ended up.
            var landed = response.RequestMessage?.RequestUri;
            if (landed is not null && !IsAllowed(landed))
            {
                response.Dispose();
                throw new HttpRequestException(
                    $"Refused the response from '{Describe(landed, url)}': update downloads must be HTTPS from a GitHub host.");
            }

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;

            response.Dispose();
            if (hop >= MaxRedirects)
                throw new HttpRequestException($"Too many redirects fetching '{url}'.");

            current = location.IsAbsoluteUri ? location : new Uri(current!, location);
        }
    }

    public static async Task<byte[]> GetBytesAsync(
        HttpClient http, string url, CancellationToken ct = default)
    {
        using var response = await GetAsync(http, url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public static async Task<string> GetStringAsync(
        HttpClient http, string url, CancellationToken ct = default)
    {
        using var response = await GetAsync(http, url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Scheme and host only: the path and query of a refused URL are
    /// the feed's to choose and have no place in a status line or log.</summary>
    private static string Describe(Uri? uri, string original) =>
        uri is { IsAbsoluteUri: true } ? $"{uri.Scheme}://{uri.Host}" : original.Length > 60 ? original[..60] + "…" : original;
}
