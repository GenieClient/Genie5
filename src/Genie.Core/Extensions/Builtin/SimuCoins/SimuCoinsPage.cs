using System.Text.RegularExpressions;

namespace Genie.Core.Extensions.Builtin.SimuCoins;

/// <summary>
/// Every piece of HTML knowledge about <c>store.play.net</c>, deliberately kept in
/// one file.
///
/// <para><b>Why this is isolated.</b> The Genie 4 plugin scraped the store with four
/// hardcoded regexes scattered across two classes, one of them pinned to a literal
/// image path (<c>sc_icon_28_w.png</c>). Any restyle on Simutronics' end yields an
/// empty capture group, and the original treated that as "zero" rather than as an
/// error — so a redesigned store page would silently report a balance of nothing at
/// all. Here every parser returns <see langword="null"/> on no-match, the caller
/// turns that into an explicit "couldn't read the store page" result, and when the
/// page does change there is exactly one file to fix.</para>
///
/// <para>These patterns are matched against markup this client does not control, so
/// they are expected to rot. They are not a stable contract.</para>
/// </summary>
internal static class SimuCoinsPage
{
    internal const string LoginUrl   = "https://store.play.net/Account/SignIn?returnURL=%2FAccount%2FSignIn";
    internal const string StoreUrl   = "https://store.play.net/";
    internal const string BalanceUrl = "https://store.play.net/store/purchase/dr";
    internal const string ClaimUrl   = "https://store.play.net/Store/ClaimReward";
    internal const string SignOutUrl = "https://store.play.net/Account/SignOut";

    private static readonly Regex TokenRx = new(
        "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(.*?)\" />",
        RegexOptions.Compiled);

    private static readonly Regex AccountRx = new(
        "<div\\s+class=\"login\\s+sans_serif\">\\s*(\\S+)\\s+",
        RegexOptions.Compiled);

    private static readonly Regex BalanceRx = new(
        "<h1 class=\"balance centered sans_serif\">You Have <span class=\"blue\">(\\d+)</span>",
        RegexOptions.Compiled);

    /// <summary>The store reuses one <c>RewardMessage</c> heading for two different
    /// states: "Subscription Reward: N Free SimuCoins" when a claim is waiting, and
    /// a human-readable countdown ("Next reward in ...") when it is not. They are
    /// matched separately rather than by one loose pattern, because confusing the
    /// two is the difference between claiming and not.</summary>
    private static readonly Regex ClaimableRx = new(
        "<h1 class=\"RewardMessage centered sans_serif\">Subscription Reward: (\\d+) Free SimuCoins</h1>",
        RegexOptions.Compiled);

    private static readonly Regex TimeRx = new(
        "<h1 class=\"RewardMessage centered sans_serif\">(.*?)</h1>",
        RegexOptions.Compiled);

    private static readonly Regex ClaimedRx = new(
        "<h1 class=\"RewardMessage centered sans_serif\">Claimed (\\d+) SimuCoin reward!</h1>",
        RegexOptions.Compiled);

    /// <summary>The ASP.NET antiforgery form token. Scraped per login attempt from
    /// the very response whose cookies will carry the POST — see the note in
    /// <see cref="SimuCoinsClient"/> about why the Genie 4 plugin's single
    /// scrape-at-startup was fragile.</summary>
    internal static string? Token(string html) => Group(TokenRx, html);

    /// <summary>Display name of the signed-in account, from the store header.</summary>
    internal static string? AccountName(string html) => Group(AccountRx, html);

    /// <summary>Current SimuCoin balance, or null when the balance heading is not
    /// where we expect it (a restyle, or a page that isn't the purchase page).</summary>
    internal static int? Balance(string html) =>
        int.TryParse(Group(BalanceRx, html), out var n) ? n : null;

    /// <summary>Size of the reward waiting to be claimed, or null when none is.</summary>
    internal static int? ClaimableAmount(string html) =>
        int.TryParse(Group(ClaimableRx, html), out var n) ? n : null;

    /// <summary>The countdown text shown when no reward is waiting, e.g.
    /// "Next reward in 3 days". Free-form prose from the store, echoed verbatim.</summary>
    internal static string? TimeRemaining(string html)
    {
        // Only meaningful when nothing is claimable: the same heading carries the
        // claim offer, and echoing "Subscription Reward: ..." as a countdown would
        // read as though the claim had been missed.
        if (ClaimableAmount(html) is not null) return null;
        var t = Group(TimeRx, html);
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    /// <summary>Amount confirmed claimed, parsed from the post-claim page.</summary>
    internal static int? ClaimedAmount(string html) =>
        int.TryParse(Group(ClaimedRx, html), out var n) ? n : null;

    private static string? Group(Regex rx, string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = rx.Match(html);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
    }
}
