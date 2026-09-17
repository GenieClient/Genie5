using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin.SimuCoins;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #328 — the SimuCoins rebuild. These cover the store conversation and the
/// command surface, and in particular the four places the Genie 4 plugin's behaviour
/// was deliberately NOT ported: the stale antiforgery token, the sign-out that signed
/// nobody out, unparseable pages reported as a zero balance, and static state shared
/// across overlapping runs.
/// </summary>
public class SimuCoinsTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private const string SignInHtml =
        "<form><input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"TOKEN-123\" /></form>";

    private static string StoreHtml(int balance, string reward) =>
        "<div class=\"login sans_serif\">MONIL  |  <a href=\"/Account/SignOut\">SIGN OUT</a></div>"
      + $"<h1 class=\"balance centered sans_serif\">You Have <span class=\"blue\">{balance}</span>"
      + "<img src=\"https://www.play.net/images/layout/store/icons/sc_icon_28_w.png\">!</h1>"
      + $"<h1 class=\"RewardMessage centered sans_serif\">{reward}</h1>";

    private const string Countdown  = "Next reward in 12 days";
    private const string Claimable  = "Subscription Reward: 100 Free SimuCoins";

    private static string ClaimedHtml(int newBalance) =>
        "<div class=\"login sans_serif\">MONIL  |  <a href=\"/Account/SignOut\">SIGN OUT</a></div>"
      + $"<h1 class=\"balance centered sans_serif\">You Have <span class=\"blue\">{newBalance}</span>"
      + "<img src=\"https://www.play.net/images/layout/store/icons/sc_icon_28_w.png\">!</h1>"
      + "<h1 class=\"RewardMessage centered sans_serif\">Claimed 100 SimuCoin reward!</h1>";

    // ── Fake transport ───────────────────────────────────────────────────────

    /// <summary>Stands in for store.play.net. Records every request so the tests can
    /// assert on the ORDER and the TRANSPORT, not just the parsed result — the
    /// sign-out fix is only observable as "it went through this handler".</summary>
    private sealed class FakeStore : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public readonly List<string> Bodies   = new();

        public bool   SignInSucceeds { get; set; } = true;
        public string StorePage      { get; set; } = StoreHtml(1234, Countdown);
        public string SignInPage     { get; set; } = SignInHtml;
        public string ClaimPage      { get; set; } = ClaimedHtml(1334);
        public HttpStatusCode ClaimStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(ct));

            HttpResponseMessage Reply(string body, HttpStatusCode code, string landedAt)
            {
                var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
                // Mirrors what HttpClient does after following redirects: the final
                // URL is what the client inspects to decide whether sign-in worked.
                r.RequestMessage = new HttpRequestMessage(request.Method, landedAt);
                return r;
            }

            if (url.StartsWith(SimuCoinsPage.LoginUrl, StringComparison.Ordinal))
                return request.Method == HttpMethod.Post
                    ? Reply("", HttpStatusCode.OK,
                            SignInSucceeds ? SimuCoinsPage.StoreUrl : SimuCoinsPage.LoginUrl)
                    : Reply(SignInPage, HttpStatusCode.OK, url);

            if (url == SimuCoinsPage.BalanceUrl) return Reply(StorePage, HttpStatusCode.OK, url);
            if (url == SimuCoinsPage.ClaimUrl)   return Reply(ClaimPage, ClaimStatus, url);
            if (url == SimuCoinsPage.SignOutUrl) return Reply("", HttpStatusCode.OK, url);

            return Reply("", HttpStatusCode.NotFound, url);
        }
    }

    private static async Task<(SimuCoinsResult Result, FakeStore Store)> Check(
        Action<FakeStore>? arrange = null, bool claim = true)
    {
        var store = new FakeStore();
        arrange?.Invoke(store);
        using var client = new SimuCoinsClient(store);
        return (await client.CheckAsync("MONIL", "hunter2", claim), store);
    }

    // ── Page parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void TheBalanceAndCountdownComeOffTheStorePage()
    {
        var html = StoreHtml(1234, Countdown);
        Assert.Equal(1234, SimuCoinsPage.Balance(html));
        Assert.Equal(Countdown, SimuCoinsPage.TimeRemaining(html));
        Assert.Null(SimuCoinsPage.ClaimableAmount(html));
        Assert.Equal("MONIL", SimuCoinsPage.AccountName(html));
    }

    [Fact]
    public void AWaitingRewardIsNotMistakenForACountdown()
    {
        // The store reuses ONE heading for both states. Reading the claim offer as
        // a countdown would echo "Subscription Reward: ..." as though the reward had
        // already come and gone.
        var html = StoreHtml(1234, Claimable);
        Assert.Equal(100, SimuCoinsPage.ClaimableAmount(html));
        Assert.Null(SimuCoinsPage.TimeRemaining(html));
    }

    [Fact]
    public void ARestyledPageParsesAsNothingRatherThanAsZero()
    {
        // The whole point of the isolation in SimuCoinsPage: the Genie 4 plugin took
        // the empty capture group and reported a balance of 0.
        const string restyled = "<div class=\"wallet\"><span>You have 1234 coins</span></div>";
        Assert.Null(SimuCoinsPage.Balance(restyled));
        Assert.Null(SimuCoinsPage.ClaimableAmount(restyled));
        Assert.Null(SimuCoinsPage.Token(restyled));
    }

    // ── The store conversation ───────────────────────────────────────────────

    [Fact]
    public async Task APlainCheckReadsTheBalanceAndSignsOut()
    {
        var (r, store) = await Check();

        Assert.Equal(SimuCoinsStatus.Ok, r.Status);
        Assert.Equal("MONIL", r.Account);
        Assert.Equal(1234, r.Balance);
        Assert.Equal(Countdown, r.TimeRemaining);
        Assert.Null(r.Claimed);
        Assert.DoesNotContain(store.Requests, q => q.Contains(SimuCoinsPage.ClaimUrl));
    }

    [Fact]
    public async Task TheTokenIsScrapedPerLoginNotOncePerSession()
    {
        // Genie 4 fetched the token ONCE at plugin startup, on a different,
        // cookie-less client, and reused it forever. Here the GET precedes the POST
        // on this same handler every time, and the scraped value is what is posted.
        var (_, store) = await Check();

        var getIndex  = store.Requests.FindIndex(q => q.StartsWith("GET "  + SimuCoinsPage.LoginUrl, StringComparison.Ordinal));
        var postIndex = store.Requests.FindIndex(q => q.StartsWith("POST " + SimuCoinsPage.LoginUrl, StringComparison.Ordinal));
        Assert.True(getIndex >= 0 && postIndex > getIndex);
        Assert.Contains(store.Bodies, b => b.Contains("TOKEN-123"));
    }

    [Fact]
    public async Task SignOutGoesThroughTheAuthenticatedHandler()
    {
        // The Genie 4 bug: SignOut built a NEW HttpClient with an empty cookie jar,
        // so it signed nobody out and the real session was left to age out. If the
        // sign-out is on this handler, it is on the jar that holds the session.
        var (_, store) = await Check();
        Assert.Contains("GET " + SimuCoinsPage.SignOutUrl, store.Requests);
        Assert.Equal(store.Requests.Count - 1,
                     store.Requests.IndexOf("GET " + SimuCoinsPage.SignOutUrl));
    }

    [Fact]
    public async Task AWaitingRewardIsClaimedAndTheNewBalanceReported()
    {
        var (r, store) = await Check(s => s.StorePage = StoreHtml(1234, Claimable));

        Assert.Equal(SimuCoinsStatus.Ok, r.Status);
        Assert.Equal(100,  r.Claimed);
        Assert.Equal(1334, r.Balance);
        Assert.Contains(store.Bodies, b => b.Contains("game=DR"));
    }

    [Fact]
    public async Task ClaimingIsSkippedWhenTheCallerAsksForABalanceOnly()
    {
        var (r, store) = await Check(s => s.StorePage = StoreHtml(1234, Claimable), claim: false);

        Assert.Equal(SimuCoinsStatus.Ok, r.Status);
        Assert.Null(r.Claimed);
        Assert.DoesNotContain(store.Requests, q => q.Contains(SimuCoinsPage.ClaimUrl));
    }

    [Fact]
    public async Task ABadPasswordIsReportedAsSuch()
    {
        var (r, _) = await Check(s => s.SignInSucceeds = false);
        Assert.Equal(SimuCoinsStatus.BadCredentials, r.Status);
    }

    [Fact]
    public async Task AMissingTokenStopsBeforeAnyCredentialsAreSent()
    {
        var (r, store) = await Check(s => s.SignInPage = "<form>nothing here</form>");

        Assert.Equal(SimuCoinsStatus.ParseFailed, r.Status);
        Assert.DoesNotContain(store.Requests, q => q.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASignedInButUnreadablePageIsAnErrorNotAZeroBalance()
    {
        var (r, _) = await Check(s => s.StorePage = "<div class=\"wallet\">1234</div>");

        Assert.Equal(SimuCoinsStatus.ParseFailed, r.Status);
        Assert.Null(r.Balance);
        Assert.Contains("redesigned", r.Detail);
    }

    [Fact]
    public async Task AnUnconfirmedClaimKeepsTheBalanceAndSaysSo()
    {
        var (r, _) = await Check(s =>
        {
            s.StorePage = StoreHtml(1234, Claimable);
            s.ClaimPage = "<h1>Something else entirely</h1>";
        });

        Assert.Equal(SimuCoinsStatus.ClaimFailed, r.Status);
        Assert.Equal(1234, r.Balance);
        Assert.Null(r.Claimed);
    }

    [Fact]
    public async Task ARejectedClaimPostIsReportedWithItsStatus()
    {
        var (r, _) = await Check(s =>
        {
            s.StorePage   = StoreHtml(1234, Claimable);
            s.ClaimStatus = HttpStatusCode.ServiceUnavailable;
        });

        Assert.Equal(SimuCoinsStatus.ClaimFailed, r.Status);
        Assert.Contains("503", r.Detail);
    }

    [Fact]
    public async Task EmptyCredentialsNeverReachTheNetwork()
    {
        var store = new FakeStore();
        using var client = new SimuCoinsClient(store);

        var r = await client.CheckAsync("", "", claim: true);

        Assert.Equal(SimuCoinsStatus.BadCredentials, r.Status);
        Assert.Empty(store.Requests);
    }

    // ── The command surface ──────────────────────────────────────────────────

    private sealed class FakeHost : IExtensionHost
    {
        public readonly ConcurrentDictionary<string, string> Vars = new();
        public readonly List<string> Echoed = new();
        public IDictionary<string, string> Globals => Vars;
        public string ConfigDir { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "genie-sc-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Echo(string text) { lock (Echoed) Echoed.Add(text); }
        public void SendCommand(string command) { }
        public void SetWindow(string window, string content) { }
        public void Log(string message) { }
    }

    private static (SimuCoinsExtension Ext, FakeHost Host, FakeStore Store) NewExtension(
        Action<FakeStore>? arrange = null, params SimuCoinsAccount[] accounts)
    {
        var host  = new FakeHost();
        var store = new FakeStore();
        arrange?.Invoke(store);

        var ext = new SimuCoinsExtension { ClientFactory = () => new SimuCoinsClient(store) };
        ext.Initialize(host);
        if (accounts.Length > 0) ext.AccountProvider = _ => accounts;
        return (ext, host, store);
    }

    /// <summary>The work is deliberately fire-and-forget so the store round-trip
    /// can't block input; the tests wait for the echo it ends with.</summary>
    private static void WaitForEcho(FakeHost host, string fragment)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (host.Echoed)
                if (host.Echoed.Any(e => e.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    return;
            Thread.Sleep(10);
        }

        lock (host.Echoed)
            Assert.Fail($"Never echoed '{fragment}'. Saw: {string.Join(" | ", host.Echoed)}");
    }

    [Theory]
    [InlineData("/sc")]
    [InlineData("/simucoins")]
    [InlineData("/sct")]
    [InlineData("/sctext")]
    [InlineData("/sca")]
    [InlineData("/scall")]
    public void EveryGenie4CommandSpellingIsClaimed(string cmd)
    {
        var (ext, host, _) = NewExtension(null, new SimuCoinsAccount("MONIL", "hunter2"));
        Assert.True(ext.OnSlashCommand(cmd));
        WaitForEcho(host, "SimuCoins");
    }

    [Fact]
    public void SomebodyElsesSlashCommandIsLeftAlone()
    {
        var (ext, _, _) = NewExtension();
        Assert.False(ext.OnSlashCommand("/calc"));
        Assert.False(ext.OnSlashCommand("/track clear"));
    }

    [Fact]
    public void AStoredAccountIsCheckedWithNoArguments()
    {
        // The whole reason this is first-party: Genie 5 already has the credentials,
        // so /sc needs no arguments and there is no second account file.
        var (ext, host, _) = NewExtension(null, new SimuCoinsAccount("MONIL", "hunter2"));

        Assert.True(ext.OnSlashCommand("/sc"));
        WaitForEcho(host, "You have 1234 SimuCoins");
        lock (host.Echoed) Assert.Contains(host.Echoed, e => e.Contains("Account: MONIL"));
    }

    [Fact]
    public void OneOffCredentialsCanBePassedInline()
    {
        var (ext, host, _) = NewExtension();   // no provider at all
        Assert.True(ext.OnSlashCommand("/sc SOMEONE else"));
        WaitForEcho(host, "You have 1234 SimuCoins");
    }

    [Fact]
    public void WithNothingSavedTheUserIsToldHowToProceed()
    {
        var (ext, host, store) = NewExtension();
        Assert.True(ext.OnSlashCommand("/sc"));
        WaitForEcho(host, "no stored password");
        Assert.Empty(store.Requests);
    }

    [Fact]
    public void AllAccountsAreSweptInOrderAndBracketedByProgress()
    {
        var (ext, host, _) = NewExtension(null,
            new SimuCoinsAccount("ONE", "a"), new SimuCoinsAccount("TWO", "b"));

        Assert.True(ext.OnSlashCommand("/sca"));
        WaitForEcho(host, "Accounts checked");

        lock (host.Echoed)
        {
            Assert.Equal("Checking accounts...", host.Echoed.First());
            // Two accounts checked means two balance reports, and the claim
            // bookkeeping from the first must not bleed into the second — the
            // Genie 4 sweep left `isClaimed` set and reported every later account
            // as freshly claimed.
            Assert.Equal(2, host.Echoed.Count(e => e.Contains("You have 1234 SimuCoins")));
            Assert.DoesNotContain(host.Echoed, e => e.Contains("You now have"));
        }
    }

    [Fact]
    public void HelpIsAvailableAndSaysNothingRunsOnItsOwn()
    {
        var (ext, host, store) = NewExtension();
        Assert.True(ext.OnSlashCommand("/sc help"));

        lock (host.Echoed)
        {
            Assert.Contains(host.Echoed, e => e.Contains("/sca, /scall"));
            Assert.Contains(host.Echoed, e => e.Contains("Nothing runs on its own"));
        }
        Assert.Empty(store.Requests);
    }

    [Fact]
    public void ADisabledExtensionClaimsNothing()
    {
        var (ext, _, _) = NewExtension(null, new SimuCoinsAccount("MONIL", "hunter2"));
        ext.Enabled = false;
        Assert.False(ext.OnSlashCommand("/sc"));
    }

    [Fact]
    public void MalformedArgumentsGetUsageRatherThanAGuess()
    {
        var (ext, host, store) = NewExtension(null, new SimuCoinsAccount("MONIL", "hunter2"));
        Assert.True(ext.OnSlashCommand("/sc onlyone"));

        lock (host.Echoed) Assert.Contains(host.Echoed, e => e.StartsWith("Usage:", StringComparison.Ordinal));
        Assert.Empty(store.Requests);
    }
}
