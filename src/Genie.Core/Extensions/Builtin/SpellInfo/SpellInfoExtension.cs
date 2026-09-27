namespace Genie.Core.Extensions.Builtin.SpellInfo;

/// <summary>
/// Built-in <b>SpellInfo</b> — the V5 port of Etherian's Genie 4 plugin (v1.1),
/// from the source he provided for the purpose (public #267). This is the lookup
/// half: a spell reference queryable from the command line.
///
/// <list type="bullet">
/// <item><c>/spellinfo name &lt;spell&gt;</c> and <c>/spellinfo abbr &lt;abbr&gt;</c> —
///   one spell's details.</item>
/// <item><c>/spellinfo guild &lt;guild&gt;</c>, <c>/spellinfo mana &lt;type&gt;</c>, and
///   <c>/spellinfo query /guild … /mana … /difficulty … /skill …</c> — lists, with
///   the filters stacking (see <see cref="SpellQuery"/>).</item>
/// <item><c>/spellinfo refresh</c> — re-download the spell list (at most daily).</item>
/// </list>
///
/// <para><b>Rows are clickable.</b> As in the Genie 4 plugin, every listed spell is a
/// <c>#link</c> whose command drills into that spell. The plugin's link ran
/// <c>#send /SpellInfo name …</c>; here the link runs <c>/spellinfo name …</c>
/// directly, because a link click already goes through the typed-input path that
/// offers <c>/</c>-commands to the built-ins, and <c>#send</c> would hold a local
/// lookup behind the game's roundtime queue for no reason.</para>
///
/// <para><b>The data is the live feed, not the plugin's snapshot.</b> The plugin
/// shipped a spell table from 2015 that its author says is long out of date. This
/// reads Etherian's nightly Elanthipedia-derived feed instead, cached through
/// <see cref="SpellFeedCache"/>: downloaded the first time a command needs it,
/// re-downloaded only once the cache is a day old, and used from disk when the
/// feed can't be reached. Nothing here runs while game text is processed.</para>
///
/// <para><b>Not yet ported:</b> the plugin's other half, which annotated other
/// characters' casts ("You recognize the familiar mnemonics of the … spell") with
/// configured spell fields. That rewrites game lines and is a separate change.</para>
/// </summary>
public sealed class SpellInfoExtension : IGameExtension
{
    /// <summary>File name of the cached feed, in the shared data folder: the spell
    /// list is the same for every character, so there is one copy.</summary>
    public const string CacheFileName = "SpellInfo.spells.json";

    /// <summary>Links to the wiki are only built for this host, so a feed that
    /// somehow carried another address can't turn a spell row into a link to it.</summary>
    private const string WikiPrefix = "https://elanthipedia.play.net/";

    private IExtensionHost? _host;
    private SpellFeedCache? _cache;
    private volatile SpellTable? _table;
    private readonly object _gate = new();
    private Task<SpellFetchResult>? _fetch;

    public string Name        => "SpellInfo";
    public string Version     => "2.0";
    public string Description => "Look up DragonRealms spells by name, abbreviation, guild, mana type, or a stacked query (/spellinfo).";
    public bool   Enabled     { get; set; } = true;

    /// <summary>Builds the cache from the data folder. Swapped by tests to inject
    /// an HTTP handler and a clock.</summary>
    internal Func<string, SpellFeedCache> CacheFactory { get; set; } =
        static dataRoot => new SpellFeedCache(Path.Combine(dataRoot, CacheFileName));

    /// <summary>The most recent background continuation (a command waiting on a
    /// download, or a silent refresh), so tests can await it.</summary>
    internal Task? PendingWork { get; private set; }

    public void Initialize(IExtensionHost host) => _host = host;

    public void OnGameLine(string line)   { }
    public void OnCommandSent(string cmd) { }
    public void OnPrompt()                { }
    public void Shutdown()                { }

    public bool OnSlashCommand(string input)
    {
        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/spellinfo", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Enabled) return false;

        var sub  = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";
        var args = parts.Skip(2).ToList();
        var rest = string.Join(" ", args);

        switch (sub)
        {
            case "help":
            case "?":
                Help();
                return true;

            case "refresh":
                Refresh();
                return true;

            case "query" when args.Count == 1 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase):
                QueryHelp();
                return true;

            case "name" or "abbr" or "guild" or "mana" when rest.Length == 0:
            {
                var what = sub switch
                {
                    "name"  => "spell name",
                    "abbr"  => "abbreviation",
                    "guild" => "guild",
                    _       => "mana type",
                };
                Echo($"Usage: /spellinfo {sub} <{what}>");
                return true;
            }

            case "name":  WithData(t => ShowByName(t, rest));   return true;
            case "abbr":  WithData(t => ShowByAbbrev(t, rest)); return true;
            case "guild": WithData(t => ShowQuery(t, new SpellFilter(Guild: rest))); return true;
            case "mana":  WithData(t => ShowQuery(t, new SpellFilter(Mana: rest)));  return true;

            case "query":
            {
                var filter = SpellQuery.Parse(args, out var error);
                if (filter is null) { Echo("SpellInfo: " + error); return true; }
                WithData(t => ShowQuery(t, filter));
                return true;
            }

            default:
                Echo($"SpellInfo: unknown option '{parts[1]}'. Type /spellinfo help.");
                return true;
        }
    }

    // ── Data ─────────────────────────────────────────────────────────────────

    private SpellFeedCache Cache =>
        _cache ??= CacheFactory(_host?.DataRoot ?? Path.GetTempPath());

    /// <summary>Run <paramref name="show"/> against the spell table. With a table in
    /// memory or on disk this is immediate (and a day-old cache is refreshed quietly
    /// in the background for next time). With neither — the very first use — it
    /// downloads first and answers when the download lands.</summary>
    private void WithData(Action<SpellTable> show)
    {
        var table = _table ??= Cache.LoadCached(Log);
        if (table is not null)
        {
            show(table);
            if (Cache.IsStale) RefreshQuietly();
            return;
        }

        Echo("SpellInfo: downloading the spell list (first use)...");
        PendingWork = StartFetch().ContinueWith(t =>
        {
            try
            {
                var r = t.Result;
                if (r.Table is not null) show(r.Table);
                else Echo($"SpellInfo: couldn't download the spell list — {r.Error}. "
                        + "Check your connection and try again, or type /spellinfo refresh.");
            }
            catch (Exception ex) { Log($"SpellInfo: {ex}"); }
        }, TaskScheduler.Default);
    }

    private void RefreshQuietly()
    {
        PendingWork = StartFetch().ContinueWith(t =>
        {
            // The command was already answered from the cache; a failed refresh
            // only means the next one is too. Worth a log line, not an echo.
            if (t.IsCompletedSuccessfully && t.Result.Error is { } err)
                Log($"SpellInfo: background refresh failed, still using the cached copy: {err}");
        }, TaskScheduler.Default);
    }

    /// <summary>One download at a time: a second command while one is in flight
    /// shares it rather than fetching again.</summary>
    private Task<SpellFetchResult> StartFetch()
    {
        lock (_gate)
        {
            if (_fetch is { IsCompleted: false } running) return running;
            var cache = Cache;
            _fetch = Task.Run(async () =>
            {
                try
                {
                    var r = await cache.FetchAsync().ConfigureAwait(false);
                    if (r.Table is not null) _table = r.Table;
                    return r;
                }
                catch (Exception ex)
                {
                    return new SpellFetchResult(null, ex.Message);
                }
            });
            return _fetch;
        }
    }

    private void Refresh()
    {
        _table ??= Cache.LoadCached(Log);
        if (_table is { } current && !Cache.IsStale)
        {
            var age = DateTime.UtcNow - (Cache.CachedAtUtc ?? DateTime.UtcNow);
            Echo($"SpellInfo: the spell list is up to date ({current.Spells.Count} spells, "
               + $"downloaded {Ago(age)}{Synced(current)}). The feed is rebuilt nightly, "
               + "so it's re-downloaded at most once a day.");
            return;
        }

        Echo("SpellInfo: downloading the spell list...");
        PendingWork = StartFetch().ContinueWith(t =>
        {
            try
            {
                var r = t.Result;
                if (r.Table is { } fresh)
                    Echo($"SpellInfo: loaded {fresh.Spells.Count} spells{Synced(fresh)}.");
                else if (_table is { } cached)
                    Echo($"SpellInfo: couldn't refresh the spell list — {r.Error}. "
                       + $"Still using the cached copy{Synced(cached)}.");
                else
                    Echo($"SpellInfo: couldn't download the spell list — {r.Error}. "
                       + "Check your connection and try again.");
            }
            catch (Exception ex) { Log($"SpellInfo: {ex}"); }
        }, TaskScheduler.Default);
    }

    // ── Output ───────────────────────────────────────────────────────────────

    private void ShowByName(SpellTable table, string typed)
    {
        var want = Normalize(typed);
        var exact = table.Spells.FirstOrDefault(s =>
            Normalize(s.Name).Equals(want, StringComparison.OrdinalIgnoreCase) ||
            Normalize(s.PageTitle).Equals(want, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) { Render(exact); return; }

        var partial = table.Spells
            .Where(s => s.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1) { Render(partial[0]); return; }
        if (partial.Count == 0)
        {
            Echo($"SpellInfo: no spell named '{typed}'. Try part of the name, or /spellinfo query.");
            return;
        }

        Echo($"SpellInfo: {partial.Count} spells match '{typed}':");
        List(partial);
    }

    private void ShowByAbbrev(SpellTable table, string typed)
    {
        var want = typed.Trim();
        var hits = table.Spells
            .Where(s => s.Abbrev is not null && s.Abbrev.Equals(want, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hits.Count == 1) { Render(hits[0]); return; }
        if (hits.Count == 0)
        {
            Echo($"SpellInfo: no spell has the abbreviation '{want}'.");
            return;
        }

        Echo($"SpellInfo: {hits.Count} spells use the abbreviation '{want}':");
        List(hits);
    }

    private void ShowQuery(SpellTable table, SpellFilter filter)
    {
        var r = SpellQuery.Run(filter, table);
        if (r.Error is not null) { Echo("SpellInfo: " + r.Error); return; }

        var criteria = string.Join(", ", r.Applied.Select(a => $"{a.Filter} {a.Value}"));
        if (r.Matches.Count == 0)
        {
            Echo($"SpellInfo: no spells match {criteria}.");
            return;
        }

        // Everything is echoed BEFORE the links: a command answered off the game
        // thread (after a first download) echoes directly while each #link is
        // posted to the game thread, so a line echoed after the links could jump
        // ahead of them.
        Echo($"SpellInfo: {r.Matches.Count} spell{(r.Matches.Count == 1 ? "" : "s")} — {criteria}. Click one for details.");
        List(r.Matches);
    }

    /// <summary>The detail view, in the Genie 4 plugin's layout, with the fields the
    /// feed adds (mana type, spellbook, spell type, slots) alongside.</summary>
    private void Render(SpellRecord s)
    {
        Echo($"Name: {s.Name}");
        Echo($"Abbr / Sig.: {s.Abbrev ?? "none"} / {s.Signature switch { true => "signature", false => "not signature", _ => "unknown" }}");
        Echo($"Guild: {s.Guild}");
        if (s.ManaType is not null || s.Spellbook is not null)
            Echo($"Mana / Book: {s.ManaType ?? "?"} / {s.Spellbook ?? "?"}");
        var diffType = string.Join(" ", new[] { s.Difficulty, string.Join(" / ", s.CastTypes) }
            .Where(x => !string.IsNullOrEmpty(x)));
        if (diffType.Length > 0) Echo($"Diff/Type: {diffType} spell.");
        if (s.Skills.Count > 0)     Echo($"Skill: {string.Join(", ", s.Skills)}");
        if (s.SpellTypes.Count > 0) Echo($"Spell Type: {string.Join(", ", s.SpellTypes)}");
        if (s.Effect is not null)   Echo($"Effect: {s.Effect}");
        if (s.MinPrep is not null || s.Cap is not null)
            Echo($"Cast Range: {s.MinPrep?.ToString() ?? "?"} - {s.Cap?.ToString() ?? "?"}");
        if (s.MinSkill is not null || s.MaxSkill is not null)
            Echo($"Rank Range: {s.MinSkill?.ToString() ?? "?"} - {s.MaxSkill?.ToString() ?? "?"}");
        if (s.Slots is not null) Echo($"Slots: {s.Slots}");
        if (s.OffContest is not null || s.DefContest is not null)
            Echo($"Contest: {s.OffContest ?? "?"} v {s.DefContest ?? "?"}");

        if (s.WikiUrl is { } url && url.StartsWith(WikiPrefix, StringComparison.OrdinalIgnoreCase)
            && !url.Any(c => char.IsWhiteSpace(c) || c is '{' or '}' or ';' or '"'))
            Link($"Elanthipedia: {s.Name}", "#browser " + url);
    }

    private void List(IEnumerable<SpellRecord> spells)
    {
        foreach (var s in spells)
            Link(s.Abbrev is null ? s.Name : $"{s.Name} ({s.Abbrev})", "/spellinfo name " + s.Name);
    }

    /// <summary>Emit a clickable line through <c>#link</c>. The text and command come
    /// from a downloaded file, so anything that could close the brace group or
    /// chain a second command on click is stripped first.</summary>
    private void Link(string text, string command) =>
        _host?.RunHashCommand($"#link {{{LinkSafe(text)}}} {{{LinkSafe(command)}}}");

    internal static string LinkSafe(string s) =>
        new(s.Where(c => c is not ('{' or '}' or ';' or '\\' or '"') && !char.IsControl(c)).ToArray());

    private void Help()
    {
        Echo("SpellInfo — look up DragonRealms spells. Data: Elanthipedia, via a feed rebuilt nightly.");
        Echo("  /spellinfo name <spell name>     details for one spell");
        Echo("  /spellinfo abbr <abbreviation>   look a spell up by its abbreviation");
        Echo("  /spellinfo guild <guild>         every spell in a guild");
        Echo("  /spellinfo mana <mana type>      every spell of a mana type");
        Echo("  /spellinfo query (/guild <guild>) (/mana <mana type>) (/difficulty <difficulty>) (/skill <skill>)");
        Echo("  /spellinfo query help            query examples");
        Echo("  /spellinfo refresh               re-download the spell list (at most once a day)");
        Echo("Click a spell in any list to see its details.");
    }

    private void QueryHelp()
    {
        Echo("SpellInfo query examples:");
        Echo("  /spellinfo query /guild warrior mage");
        Echo("  /spellinfo query /mana life");
        Echo("  /spellinfo query /mana elemental /difficulty intro /skill targeted");
        Echo("Filters stack, and any unambiguous start of a value works (/guild moon, /skill aug).");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Normalize(string s) =>
        string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Ago(TimeSpan age) =>
        age.TotalMinutes < 1 ? "just now"
      : age.TotalHours   < 1 ? $"{(int)age.TotalMinutes} min ago"
      :                        $"{(int)age.TotalHours} h ago";

    private static string Synced(SpellTable t) =>
        t.SyncedAt is { } at ? $", wiki data from {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "";

    /// <summary>Display only. Spell details are reference text, not game output, so
    /// they are kept out of the trigger/action feed — an effect line like "Fire
    /// damage" must not trip a hunting script's trigger.</summary>
    private void Echo(string text) => _host?.EchoRouted(text, display: true, parse: false);

    private void Log(string text) => _host?.Log(text);
}
