namespace Genie.Core.Extensions.Builtin.SimuCoins;

/// <summary>One play.net account the SimuCoins commands may sign in as.</summary>
public sealed record SimuCoinsAccount(string Account, string Password);

/// <summary>
/// Built-in <b>SimuCoins</b> — the V5 rebuild of Thires's Genie 4 plugin (v2.1.2,
/// GPL-3.0, rebuilt with his blessing; public #328).
///
/// <list type="bullet">
/// <item><c>/sc</c> — check the account you are connected as.</item>
/// <item><c>/sc &lt;account&gt; &lt;password&gt;</c> — check some other account, one
///   off. Nothing is stored.</item>
/// <item><c>/sct</c>, <c>/sctext</c> — aliases of <c>/sc</c>, for Genie 4 muscle
///   memory. The Genie 4 plugin split these because <c>/sc</c> opened a WinForms
///   window and <c>/sct</c> was the text-only path; there is no separate window
///   here, so both do the same thing.</item>
/// <item><c>/sca</c>, <c>/scall</c> — every distinct account across your saved
///   profiles.</item>
/// </list>
///
/// <para><b>No second credential store.</b> The Genie 4 plugin kept its own
/// <c>SimuCoins.xml</c> of accounts, obfuscated by an <c>EncryptDecrypt</c> helper
/// that wrote the AES key alongside the ciphertext — which is not encryption. That
/// file is deliberately not ported. Genie 5 already holds these exact credentials in
/// the profile store, so the accounts arrive through
/// <see cref="AccountProvider"/>, wired by the host, and <c>/sca</c> comes free.</para>
///
/// <para><b>Always user-initiated.</b> Nothing here runs on a timer, at login, or on
/// a trigger. The Genie 4 README suggested a <c>^Welcome to DragonRealms</c> trigger
/// firing <c>#put /sca</c> automatically; that is deliberately not shipped and not
/// suggested. <c>docs/POLICY.md</c> scopes Genie's hard nevers to the client's own
/// in-game conduct, and while a web-store request is not in the game stream, the
/// same principle — the client does not act on its own — is the reason this only
/// ever happens because someone typed a command. Anyone who wants it automated can
/// still write that trigger themselves; that is their call to make, not a default
/// the client ships.</para>
/// </summary>
public sealed class SimuCoinsExtension : IGameExtension
{
    private IExtensionHost? _host;

    /// <summary>Guards against overlapping runs. The Genie 4 plugin fired logins off
    /// as unawaited tasks over shared static state, so a second command mid-sweep
    /// interleaved its output with the first and corrupted the claim bookkeeping.</summary>
    private int _running;

    public string Name        => "SimuCoins";
    public string Version     => "1.0.0";
    public string Description => "Check your SimuCoin balance and claim a waiting subscription reward.";
    public bool   Enabled     { get; set; } = true;

    /// <summary>Supplies play.net credentials. Called with <c>true</c> for every
    /// distinct saved account (<c>/sca</c>) and <c>false</c> for just the connected
    /// one (<c>/sc</c>). Wired by the host, because the profile store lives in the
    /// app layer; left null, the credential-free commands simply report that there
    /// is nothing saved to check.</summary>
    public Func<bool, IReadOnlyList<SimuCoinsAccount>>? AccountProvider { get; set; }

    /// <summary>Swapped by tests. Real runs get a fresh client per account so a
    /// session cannot leak from one account into the next.</summary>
    internal Func<SimuCoinsClient> ClientFactory { get; set; } = static () => new SimuCoinsClient();

    public void Initialize(IExtensionHost host) => _host = host;

    public void OnGameLine(string line)    { }
    public void OnCommandSent(string cmd)  { }
    public void OnPrompt()                 { }
    public void Shutdown()                 { }

    public bool OnSlashCommand(string input)
    {
        var t = input.Trim();
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var verb = parts[0].ToLowerInvariant();
        var mine = verb is "/sc" or "/simucoins" or "/sct" or "/sctext" or "/sca" or "/scall";
        if (!mine) return false;
        if (!Enabled) return false;

        if (parts.Length == 2 && parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            Help();
            return true;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Echo("SimuCoins: still working on the last request.");
            return true;
        }

        var all = verb is "/sca" or "/scall";

        // One-off credentials: /sc <account> <password>. Not stored, by design.
        if (!all && parts.Length == 3)
        {
            Run(new[] { new SimuCoinsAccount(parts[1], parts[2]) });
            return true;
        }

        if (!all && parts.Length != 1)
        {
            Echo("Usage: /sc  or  /sc <account> <password>  or  /sca for every saved account.");
            Interlocked.Exchange(ref _running, 0);
            return true;
        }

        var accounts = AccountProvider?.Invoke(all) ?? Array.Empty<SimuCoinsAccount>();
        if (accounts.Count == 0)
        {
            Echo(all
                ? "SimuCoins: no saved accounts with a stored password to check."
                : "SimuCoins: no stored password for the connected account. "
                + "Use /sc <account> <password>, or save the password in the profile.");
            Interlocked.Exchange(ref _running, 0);
            return true;
        }

        Run(accounts);
        return true;
    }

    private void Run(IReadOnlyList<SimuCoinsAccount> accounts)
    {
        // Fire-and-forget on purpose: the store round-trip takes seconds and must
        // never block input. Every failure path inside is caught, so the task is
        // not left unobserved.
        _ = Task.Run(async () =>
        {
            try
            {
                if (accounts.Count > 1) Echo("Checking accounts...");

                foreach (var acct in accounts)
                {
                    using var client = ClientFactory();
                    Report(await client.CheckAsync(acct.Account, acct.Password).ConfigureAwait(false));
                }

                if (accounts.Count > 1) Echo("Accounts checked.");
            }
            catch (Exception ex)
            {
                _host?.Log($"SimuCoins: {ex}");
                Echo($"SimuCoins: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private void Report(SimuCoinsResult r)
    {
        switch (r.Status)
        {
            case SimuCoinsStatus.Ok:
                Echo($"Account: {r.Account}");
                if (r.Claimed is { } got)
                {
                    Echo($"Claimed {got} SimuCoins.");
                    Echo($"You now have {r.Balance} SimuCoins.");
                }
                else
                {
                    Echo($"You have {r.Balance} SimuCoins.");
                    if (r.TimeRemaining is { } when) Echo(when);
                }
                break;

            case SimuCoinsStatus.BadCredentials:
                Echo($"Account: {r.Account} — incorrect account name or password.");
                break;

            case SimuCoinsStatus.ParseFailed:
                Echo($"Account: {r.Account} — couldn't read the store page. "
                   + (r.Detail ?? string.Empty));
                break;

            case SimuCoinsStatus.ClaimFailed:
                Echo($"Account: {r.Account} — you have {r.Balance} SimuCoins, but the "
                   + "reward claim didn't go through. " + (r.Detail ?? string.Empty));
                break;

            case SimuCoinsStatus.NoConnection:
                Echo($"Account: {r.Account} — couldn't reach store.play.net.");
                break;

            case SimuCoinsStatus.Timeout:
                Echo($"Account: {r.Account} — store.play.net didn't answer in time.");
                break;
        }
    }

    private void Help()
    {
        Echo("SimuCoins — check your balance and claim a waiting subscription reward.");
        Echo("  /sc                        the account you're connected as");
        Echo("  /sc <account> <password>   some other account, one off (not saved)");
        Echo("  /sct, /sctext              same as /sc (Genie 4 aliases)");
        Echo("  /sca, /scall               every distinct saved account");
        Echo("A waiting reward is claimed automatically when a check finds one.");
        Echo("Nothing runs on its own — only when you type one of these.");
    }

    private void Echo(string text) => _host?.Echo(text);
}
