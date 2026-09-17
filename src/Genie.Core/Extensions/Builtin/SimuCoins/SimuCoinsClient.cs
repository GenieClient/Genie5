using System.Net;

namespace Genie.Core.Extensions.Builtin.SimuCoins;

/// <summary>How a single account check ended.</summary>
internal enum SimuCoinsStatus
{
    /// <summary>Signed in and read the store page.</summary>
    Ok,
    /// <summary>The store did not redirect to the signed-in landing page.</summary>
    BadCredentials,
    /// <summary>Signed in, but the page no longer parses — almost certainly a
    /// restyle rather than anything wrong with the account. Reported explicitly
    /// instead of being shown as a zero balance.</summary>
    ParseFailed,
    /// <summary>A reward was waiting but the claim POST did not confirm it.</summary>
    ClaimFailed,
    /// <summary>No route to store.play.net.</summary>
    NoConnection,
    /// <summary>The store did not answer within the timeout.</summary>
    Timeout,
}

/// <summary>Outcome of one account check.</summary>
/// <param name="Account">Account name as the store reports it, falling back to the
/// name used to sign in when the header could not be parsed.</param>
/// <param name="Balance">Balance after any claim, or null when unreadable.</param>
/// <param name="TimeRemaining">Store countdown text when no reward was waiting.</param>
/// <param name="Claimed">Amount claimed on this run, when a reward was waiting.</param>
internal sealed record SimuCoinsResult(
    SimuCoinsStatus Status,
    string          Account,
    int?            Balance        = null,
    string?         TimeRemaining  = null,
    int?            Claimed        = null,
    string?         Detail         = null);

/// <summary>
/// The <c>store.play.net</c> conversation, lifted from Thires's Genie 4 SimuCoins
/// plugin (GPL-3.0, rebuilt with his blessing) and corrected in four places.
///
/// <para><b>The flow.</b> GET the sign-in page, POST credentials with the
/// antiforgery token scraped from it, confirm success by the response's final
/// <c>RequestUri</c>, GET the DR purchase page for balance / countdown / pending
/// reward, POST the claim when one is waiting, then sign out.</para>
///
/// <para><b>What was fixed on the way across.</b></para>
/// <list type="number">
/// <item><b>The token is now scraped per login, on the client that owns the
///   cookies.</b> The original grabbed it once at plugin startup using a separate,
///   cookie-less <c>HttpClient</c> and reused that one value for every later login.
///   It works only because the store does not currently pair the form token with
///   the antiforgery cookie; it goes stale on a long session and breaks outright
///   the day that pairing is enforced.</item>
/// <item><b>Sign-out actually signs out.</b> The original built a brand-new client
///   with an empty <see cref="CookieContainer"/> to call <c>/Account/SignOut</c>,
///   so it sent an unauthenticated request and the real session was left to age
///   out. Here it goes through the same handler that holds the session.</item>
/// <item><b>Unparseable pages are an error, not a zero.</b> See
///   <see cref="SimuCoinsPage"/>.</item>
/// <item><b>No shared mutable state.</b> The original kept <c>noShowEcho</c> and
///   <c>isClaimed</c> in static fields while firing logins off as unawaited tasks,
///   so two overlapping runs corrupted each other's output, and a claim on one
///   account made every later account in the same sweep report as freshly claimed.
///   Each call here is self-contained.</item>
/// </list>
///
/// <para>One instance holds one cookie jar and is good for one account. The
/// extension creates a fresh one per account, so a sweep cannot leak a session
/// from one account into the next.</para>
/// </summary>
internal sealed class SimuCoinsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool       _ownsHandler;

    /// <param name="handler">Injected by tests. When null, a real handler with its
    /// own cookie jar is created — the cookies are what carry the session between
    /// the sign-in POST and the store GET, so this is never optional in practice.</param>
    internal SimuCoinsClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _ownsHandler = handler is null;
        handler ??= new HttpClientHandler
        {
            CookieContainer  = new CookieContainer(),
            UseCookies       = true,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler, disposeHandler: _ownsHandler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Sign in, read the balance, and claim a waiting reward when
    /// <paramref name="claim"/> is set. Always attempts sign-out afterwards.</summary>
    internal async Task<SimuCoinsResult> CheckAsync(
        string account, string password, bool claim = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            return new SimuCoinsResult(SimuCoinsStatus.BadCredentials, account,
                Detail: "No account name or password.");

        try
        {
            var signIn = await _http.GetAsync(SimuCoinsPage.LoginUrl, ct).ConfigureAwait(false);
            var token  = SimuCoinsPage.Token(await signIn.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (token is null)
                return new SimuCoinsResult(SimuCoinsStatus.ParseFailed, account,
                    Detail: "The sign-in page did not carry a verification token.");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["UserName"]                   = account,
                ["Password"]                   = password,
                ["RememberMe"]                 = "true",
            });

            var posted = await _http.PostAsync(SimuCoinsPage.LoginUrl, form, ct).ConfigureAwait(false);

            // The store answers a failed sign-in by re-rendering the sign-in form at
            // the same URL, so the landing URL — not the status code — is the signal.
            var landed = posted.RequestMessage?.RequestUri?.ToString();
            if (!string.Equals(landed, SimuCoinsPage.StoreUrl, StringComparison.OrdinalIgnoreCase))
                return new SimuCoinsResult(SimuCoinsStatus.BadCredentials, account);

            return await ReadStoreAsync(account, claim, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new SimuCoinsResult(SimuCoinsStatus.NoConnection, account, Detail: ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SimuCoinsResult(SimuCoinsStatus.Timeout, account);
        }
        finally
        {
            await SignOutAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<SimuCoinsResult> ReadStoreAsync(string account, bool claim, CancellationToken ct)
    {
        var page = await _http.GetStringAsync(SimuCoinsPage.BalanceUrl, ct).ConfigureAwait(false);
        var name = SimuCoinsPage.AccountName(page) ?? account;
        var balance = SimuCoinsPage.Balance(page);

        if (balance is null)
            return new SimuCoinsResult(SimuCoinsStatus.ParseFailed, name,
                Detail: "Signed in, but the store page no longer reads as expected. "
                      + "This usually means the page was redesigned.");

        var claimable = SimuCoinsPage.ClaimableAmount(page);
        if (claimable is null || !claim)
            return new SimuCoinsResult(SimuCoinsStatus.Ok, name, balance,
                SimuCoinsPage.TimeRemaining(page));

        var claimForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["game"]       = "DR",
            ["filter"]     = string.Empty,
            ["itemSearch"] = string.Empty,
        });

        var claimed = await _http.PostAsync(SimuCoinsPage.ClaimUrl, claimForm, ct).ConfigureAwait(false);
        if (!claimed.IsSuccessStatusCode)
            return new SimuCoinsResult(SimuCoinsStatus.ClaimFailed, name, balance,
                Detail: $"The store answered {(int)claimed.StatusCode} to the claim.");

        var claimedPage = await claimed.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var got = SimuCoinsPage.ClaimedAmount(claimedPage);
        if (got is null)
            return new SimuCoinsResult(SimuCoinsStatus.ClaimFailed, name, balance,
                Detail: "The store did not confirm the claim.");

        // The post-claim page carries the updated balance; fall back to the
        // pre-claim figure plus the claim when it doesn't parse.
        return new SimuCoinsResult(SimuCoinsStatus.Ok, name,
            SimuCoinsPage.Balance(claimedPage) ?? balance + got, Claimed: got);
    }

    /// <summary>Best-effort sign-out on the authenticated handler. Failures are
    /// swallowed: the check itself already succeeded or failed on its own terms,
    /// and a sign-out error is not worth overwriting that result with.</summary>
    private async Task SignOutAsync(CancellationToken ct)
    {
        try   { await _http.GetAsync(SimuCoinsPage.SignOutUrl, ct).ConfigureAwait(false); }
        catch { /* best effort */ }
    }

    public void Dispose() => _http.Dispose();
}
