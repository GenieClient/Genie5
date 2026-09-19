using System.Text.RegularExpressions;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Parsing;
using Genie.Core.Presets;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Import;

/// <summary>
/// How an import should fold incoming rules into an engine that already has
/// data. <see cref="Merge"/> matches the single-file import behaviour used by
/// every panel: matching keys get replaced, non-matching keys are appended.
/// </summary>
public enum ImportMode
{
    /// <summary>Keep existing items; keys that match are overwritten from the cfg.</summary>
    Merge,
    /// <summary>Keep existing items; only append items whose keys aren't already present.</summary>
    AddOnly,
    /// <summary>Clear the engine first, then append everything from the cfg.</summary>
    Replace,
}

/// <summary>Why a line did not make it into an engine.</summary>
public enum ImportSkipKind
{
    /// <summary>
    /// The line carried a rule and that rule was lost. This is the case the
    /// user has to be told about.
    /// </summary>
    Dropped,

    /// <summary>
    /// The line was skipped for an expected reason — already present in
    /// Add-only mode, an <c>#alias delete</c> record, the implicit
    /// <c>default</c> class. Nothing was lost.
    /// </summary>
    ByDesign,
}

/// <summary>One skipped line, with enough detail to act on it.</summary>
public readonly record struct ImportSkip(
    int LineNumber,
    string Line,
    string Reason,
    ImportSkipKind Kind)
{
    /// <summary>Single-line form for a report: <c>line 42: reason — text</c>.</summary>
    public override string ToString()
    {
        var text = Line.Length > 120 ? Line[..120] + "…" : Line;
        return $"line {LineNumber}: {Reason} — {text}";
    }
}

/// <summary>Per-file counts returned by each <c>ImportX</c> method.</summary>
/// <remarks>
/// <see cref="Skipped"/> was originally the whole story, which is how a long
/// list of silent import bugs survived: the dialog reported "N imported,
/// M skipped" and a user had no way to tell whether those M were duplicates
/// they expected or rules that had just been destroyed. <see cref="Skips"/>
/// carries the per-line reason so the report can say which.
/// </remarks>
public readonly record struct ImportResult(int Imported, int Skipped)
{
    private readonly IReadOnlyList<ImportSkip>? _skips;

    /// <summary>Every skipped line, in file order.</summary>
    public IReadOnlyList<ImportSkip> Skips
    {
        get  => _skips ?? Array.Empty<ImportSkip>();
        init => _skips = value;
    }

    /// <summary>The skips that actually lost a rule.</summary>
    public IEnumerable<ImportSkip> Dropped =>
        Skips.Where(s => s.Kind == ImportSkipKind.Dropped);

    /// <summary>How many rules were lost, as opposed to skipped by design.</summary>
    public int DroppedCount => Skips.Count(s => s.Kind == ImportSkipKind.Dropped);
}

/// <summary>Aggregated results from a full-directory import.</summary>
public sealed class ImportAllResult
{
    public ImportResult Aliases      { get; set; }
    public ImportResult Triggers     { get; set; }
    public ImportResult Highlights   { get; set; }
    public ImportResult Substitutes  { get; set; }
    public ImportResult Gags         { get; set; }
    public ImportResult Macros       { get; set; }
    public ImportResult Names        { get; set; }
    public ImportResult Presets      { get; set; }
    public ImportResult Variables    { get; set; }
    public ImportResult Classes      { get; set; }
    public ImportResult Settings     { get; set; }

    /// <summary>Config types the caller selected that had no matching file on disk.</summary>
    public List<string> MissingFiles { get; } = new();

    /// <summary>Every per-type result, labelled, in report order.</summary>
    public IEnumerable<(string Label, ImportResult Result)> All =>
    [
        ("Highlights",  Highlights),
        ("Triggers",    Triggers),
        ("Substitutes", Substitutes),
        ("Gags",        Gags),
        ("Aliases",     Aliases),
        ("Macros",      Macros),
        ("Names",       Names),
        ("Presets",     Presets),
        ("Variables",   Variables),
        ("Classes",     Classes),
        ("Settings",    Settings),
    ];

    /// <summary>Total rules lost across every type.</summary>
    public int DroppedCount => All.Sum(x => x.Result.DroppedCount);

    /// <summary>True when any rule was lost — the condition worth surfacing.</summary>
    public bool AnyDropped => DroppedCount > 0;
}

/// <summary>Everything a full-directory import needs in one place.</summary>
public sealed class Genie4ImportContext
{
    public required AliasEngine          Aliases      { get; init; }
    public required TriggerEngineFinal   Triggers     { get; init; }
    public required HighlightEngine      Highlights   { get; init; }
    public required SubstituteEngine     Substitutes  { get; init; }
    public required GagEngine            Gags         { get; init; }
    public required MacroEngine          Macros       { get; init; }
    public required NameHighlightEngine  Names        { get; init; }
    public required PresetEngine         Presets      { get; init; }
    public required VariableStore        Variables    { get; init; }
    public required ClassEngine          Classes      { get; init; }
    public required Config.GenieConfig   Settings     { get; init; }
}

/// <summary>
/// Flags controlling which config types a full-directory import touches.
/// Lets the user deselect types that shouldn't be overwritten.
/// </summary>
[Flags]
public enum Genie4ImportTypes
{
    None        = 0,
    Aliases     = 1 << 0,
    Triggers    = 1 << 1,
    Highlights  = 1 << 2,
    Substitutes = 1 << 3,
    Gags        = 1 << 4,
    Macros      = 1 << 5,
    Names       = 1 << 6,
    Presets     = 1 << 7,
    Variables   = 1 << 8,
    Classes     = 1 << 9,
    Settings    = 1 << 10,
    All         = Aliases | Triggers | Highlights | Substitutes | Gags
                | Macros  | Names    | Presets    | Variables   | Classes
                | Settings,
}

/// <summary>
/// Parses Genie4-format .cfg files and applies them to the equivalent Genie5
/// engines. Every method honours an <see cref="ImportMode"/> so callers can
/// choose between additive merge, replace-all, or skip-if-present semantics.
/// </summary>
public static class Genie4Importer
{
    // ── Entry points ────────────────────────────────────────────────────────

    /// <summary>
    /// Imports every <c>*.cfg</c> file found in <paramref name="directory"/>
    /// matching the selected <paramref name="types"/>. Files that aren't
    /// present are recorded in <see cref="ImportAllResult.MissingFiles"/> and
    /// skipped silently — a missing gags.cfg shouldn't abort the whole import.
    /// </summary>
    public static ImportAllResult ImportDirectory(
        string directory,
        Genie4ImportContext ctx,
        ImportMode mode,
        Genie4ImportTypes types = Genie4ImportTypes.All)
    {
        var result = new ImportAllResult();

        if (types.HasFlag(Genie4ImportTypes.Aliases))
            RunIfExists(directory, "aliases.cfg",     p => result.Aliases     = ImportAliases    (p, ctx.Aliases,     mode), result, "aliases.cfg");
        if (types.HasFlag(Genie4ImportTypes.Triggers))
            RunIfExists(directory, "triggers.cfg",    p => result.Triggers    = ImportTriggers   (p, ctx.Triggers,    mode), result, "triggers.cfg");
        if (types.HasFlag(Genie4ImportTypes.Highlights))
            RunIfExists(directory, "highlights.cfg",  p => result.Highlights  = ImportHighlights (p, ctx.Highlights,  mode), result, "highlights.cfg");
        if (types.HasFlag(Genie4ImportTypes.Substitutes))
            RunIfExists(directory, "substitutes.cfg", p => result.Substitutes = ImportSubstitutes(p, ctx.Substitutes, mode), result, "substitutes.cfg");
        if (types.HasFlag(Genie4ImportTypes.Gags))
            RunIfExists(directory, "gags.cfg",        p => result.Gags        = ImportGags       (p, ctx.Gags,        mode), result, "gags.cfg");
        if (types.HasFlag(Genie4ImportTypes.Macros))
            RunIfExists(directory, "macros.cfg",      p => result.Macros      = ImportMacros     (p, ctx.Macros,      mode), result, "macros.cfg");
        if (types.HasFlag(Genie4ImportTypes.Names))
            RunIfExists(directory, "names.cfg",       p => result.Names       = ImportNames      (p, ctx.Names,       mode), result, "names.cfg");
        if (types.HasFlag(Genie4ImportTypes.Presets))
            RunIfExists(directory, "presets.cfg",     p => result.Presets     = ImportPresets    (p, ctx.Presets,     mode), result, "presets.cfg");
        if (types.HasFlag(Genie4ImportTypes.Variables))
            RunIfExists(directory, "variables.cfg",   p => result.Variables   = ImportVariables  (p, ctx.Variables,   mode), result, "variables.cfg");
        if (types.HasFlag(Genie4ImportTypes.Classes))
            RunIfExists(directory, "classes.cfg",     p => result.Classes     = ImportClasses    (p, ctx.Classes,     mode), result, "classes.cfg");
        if (types.HasFlag(Genie4ImportTypes.Settings))
            RunIfExists(directory, "settings.cfg",    p => result.Settings    = ImportSettings   (p, ctx.Settings,    mode), result, "settings.cfg");

        return result;
    }

    private static void RunIfExists(string dir, string filename, Action<string> run, ImportAllResult agg, string label)
    {
        var path = Path.Combine(dir, filename);
        if (File.Exists(path)) run(path);
        else                    agg.MissingFiles.Add(label);
    }

    // Reports how many lines each file would contribute without touching engines.
    // Used by the dialog's preview so the user sees counts before committing.
    public static Dictionary<Genie4ImportTypes, int> ProbeDirectory(string directory)
    {
        var counts = new Dictionary<Genie4ImportTypes, int>();
        void Probe(Genie4ImportTypes t, string file, string directive)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) return;
            counts[t] = CountDirective(path, directive);
        }

        Probe(Genie4ImportTypes.Aliases,     "aliases.cfg",     "#alias");
        Probe(Genie4ImportTypes.Triggers,    "triggers.cfg",    "#trigger");
        Probe(Genie4ImportTypes.Highlights,  "highlights.cfg",  "#highlight");
        Probe(Genie4ImportTypes.Substitutes, "substitutes.cfg", "#subs");
        Probe(Genie4ImportTypes.Gags,        "gags.cfg",        "#gag");
        Probe(Genie4ImportTypes.Macros,      "macros.cfg",      "#macro");
        Probe(Genie4ImportTypes.Names,       "names.cfg",       "#name");
        Probe(Genie4ImportTypes.Presets,     "presets.cfg",     "#preset");
        Probe(Genie4ImportTypes.Variables,   "variables.cfg",   "#var");
        Probe(Genie4ImportTypes.Classes,     "classes.cfg",     "#class");
        Probe(Genie4ImportTypes.Settings,    "settings.cfg",    "#config");
        return counts;
    }

    private static int CountDirective(string path, string directive)
    {
        int n = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (line.StartsWith(directive, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    // ── Skip accounting ─────────────────────────────────────────────────────

    /// <summary>
    /// Accumulates the skipped lines of one file so the caller can report what
    /// was lost instead of only how much.
    /// </summary>
    private sealed class SkipLog
    {
        private readonly List<ImportSkip> _items = new();

        public IReadOnlyList<ImportSkip> Items => _items;

        /// <summary>Total skips — what the old bare counter reported.</summary>
        public int Count => _items.Count;

        /// <summary>Record a line whose rule was lost.</summary>
        public void Drop(int line, string text, string reason) =>
            _items.Add(new ImportSkip(line, text, reason, ImportSkipKind.Dropped));

        /// <summary>Record a line skipped for an expected reason.</summary>
        public void ByDesign(int line, string text, string reason) =>
            _items.Add(new ImportSkip(line, text, reason, ImportSkipKind.ByDesign));
    }

    // ── Directive tokenizing ────────────────────────────────────────────────

    /// <summary>
    /// Split a <c>*.cfg</c> directive line into its <c>{braced}</c> arguments,
    /// returning null when the line is not that directive.
    /// </summary>
    /// <remarks>
    /// Each rule type used to have its own regex with <c>[^{}]*</c> for the
    /// payload, which rejected any rule whose own text contained a brace —
    /// and a Genie 4 payload very often does, because <c>#if</c>, <c>#eval</c>
    /// and friends take braced arguments:
    /// <code>
    /// #trigger {^(\w+) appears to be aiming at you} {#if {$guild = Paladin} "#send glyph ward $1"}
    /// </code>
    /// Those lines were counted as "skipped" and silently never imported —
    /// 9 triggers and 2 macros in the reference settings tree. Worse, the
    /// runtime parses the very same text with
    /// <see cref="ArgumentParser.ParseArgs"/> when a <c>.cfg</c> is replayed
    /// through the command engine, so the importer and the client disagreed
    /// about what a valid rule was. Using the one tokenizer for both removes
    /// the divergence as well as the bug.
    /// </remarks>
    private static IReadOnlyList<string>? DirectiveArgs(string line, string verb)
    {
        var tokens = ArgumentParser.ParseArgs(line);
        if (tokens.Count == 0 || !tokens[0].Equals(verb, StringComparison.OrdinalIgnoreCase))
            return null;
        return tokens.Count == 1 ? [] : tokens.Skip(1).ToList();
    }

    /// <summary>Argument at <paramref name="index"/>, or "" when absent.</summary>
    private static string Arg(IReadOnlyList<string> args, int index) =>
        index >= 0 && index < args.Count ? args[index] : string.Empty;

    // ── Aliases ─────────────────────────────────────────────────────────────

    private static readonly Regex AliasPattern = new(
        @"^\s*#alias(?:\s+(?<verb>add|delete))?\s+\{(?<name>[^}]*)\}\s+\{(?<expansion>.*)\}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportAliases(string path, AliasEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Aliases.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#alias", StringComparison.OrdinalIgnoreCase)) continue;

            var m = AliasPattern.Match(line);
            if (!m.Success) { log.Drop(lineNo, line, "not a recognised #alias directive"); continue; }
            if (m.Groups["verb"].Value.Equals("delete", StringComparison.OrdinalIgnoreCase)) { log.ByDesign(lineNo, line, "#alias delete line - nothing to import"); continue; }

            var name      = m.Groups["name"].Value;
            var expansion = m.Groups["expansion"].Value;
            if (string.IsNullOrEmpty(name)) { log.Drop(lineNo, line, "alias name is empty"); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(name)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            engine.RemoveAlias(name);
            engine.AddAlias(name, expansion, isEnabled: true);
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Triggers ────────────────────────────────────────────────────────────

    public static ImportResult ImportTriggers(string path, TriggerEngineFinal engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Triggers.Select(t => t.Pattern), StringComparer.Ordinal);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#trigger", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#trigger");
            if (args is null || args.Count < 2) { log.Drop(lineNo, line, "not a recognised #trigger directive"); continue; }

            var pat = args[0];
            bool caseInsensitive = false;
            // Genie4 accepts both evaluated triggers (e/.../) and inline /.../i case-insensitive markers.
            if (pat.StartsWith("e/", StringComparison.OrdinalIgnoreCase) && pat.EndsWith('/'))
                pat = pat[2..^1];
            else
            {
                if (pat.StartsWith('/')) pat = pat[1..];
                if (pat.EndsWith("/i", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; pat = pat[..^2]; }
                else if (pat.EndsWith('/')) pat = pat[..^1];
            }

            if (string.IsNullOrEmpty(pat)) { log.Drop(lineNo, line, "trigger pattern is empty"); continue; }
            try { _ = new Regex(pat); }
            catch (RegexParseException ex) { log.Drop(lineNo, line, "pattern is not a valid regular expression: " + ex.Message); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(pat)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            var action = Arg(args, 1);
            var cls    = Arg(args, 2);

            engine.RemoveTrigger(pat);
            engine.AddTrigger(pat, action, caseSensitive: !caseInsensitive, isEnabled: true, className: cls);
            existing.Add(pat);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Highlights ──────────────────────────────────────────────────────────

    public static ImportResult ImportHighlights(string path, HighlightEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Pattern), StringComparer.Ordinal);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#highlight", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#highlight");
            if (args is null || args.Count < 3) { log.Drop(lineNo, line, "not a recognised #highlight directive"); continue; }

            var matchType = ParseMatchType(args[0]);
            if (matchType is null) { log.Drop(lineNo, line, "unknown highlight type"); continue; }

            var (fg, bg)    = ParseColorPair(args[1]);
            var rulePattern = args[2];
            if (string.IsNullOrEmpty(rulePattern) || string.IsNullOrEmpty(fg)) { log.Drop(lineNo, line, "highlight pattern or foreground colour is empty"); continue; }

            if (matchType == HighlightMatchType.Regex)
            {
                try { _ = new Regex(rulePattern); }
                catch (RegexParseException ex) { log.Drop(lineNo, line, "pattern is not a valid regular expression: " + ex.Message); continue; }
            }

            if (mode == ImportMode.AddOnly && existing.Contains(rulePattern)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            var cls = Arg(args, 3);

            engine.RemoveRule(rulePattern);
            engine.AddRule(rulePattern, fg, bg, matchType.Value, caseSensitive: false, isEnabled: true, className: cls);
            existing.Add(rulePattern);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    private static HighlightMatchType? ParseMatchType(string raw) =>
        raw.Trim().ToLowerInvariant() switch
        {
            "string"     => HighlightMatchType.String,
            "line"       => HighlightMatchType.Line,
            "beginswith" => HighlightMatchType.BeginsWith,
            "regexp"     => HighlightMatchType.Regex,
            "regex"      => HighlightMatchType.Regex,
            _            => null,
        };

    // ── Substitutes ─────────────────────────────────────────────────────────

    public static ImportResult ImportSubstitutes(string path, SubstituteEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Pattern), StringComparer.Ordinal);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#subs", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#subs");
            if (args is null || args.Count < 2) { log.Drop(lineNo, line, "not a recognised #subs directive"); continue; }

            var pat = args[0];
            bool caseInsensitive = false;
            if (pat.StartsWith('/')) pat = pat[1..];
            if (pat.EndsWith("/i", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; pat = pat[..^2]; }
            else if (pat.EndsWith('/')) pat = pat[..^1];

            if (string.IsNullOrEmpty(pat)) { log.Drop(lineNo, line, "substitute pattern is empty"); continue; }
            try { _ = new Regex(pat); }
            catch (RegexParseException ex) { log.Drop(lineNo, line, "pattern is not a valid regular expression: " + ex.Message); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(pat)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            var repl = Arg(args, 1);
            var cls  = Arg(args, 2);

            engine.RemoveRule(pat);
            engine.AddRule(pat, repl, caseSensitive: !caseInsensitive, isEnabled: true, className: cls);
            existing.Add(pat);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Gags ────────────────────────────────────────────────────────────────

    public static ImportResult ImportGags(string path, GagEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Pattern), StringComparer.Ordinal);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#gag", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#gag");
            if (args is null || args.Count < 1) { log.Drop(lineNo, line, "not a recognised #gag directive"); continue; }

            var pat = args[0];
            bool caseInsensitive = false;
            if (pat.StartsWith('/')) pat = pat[1..];
            if (pat.EndsWith("/i", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; pat = pat[..^2]; }
            else if (pat.EndsWith('/')) pat = pat[..^1];

            if (string.IsNullOrEmpty(pat)) { log.Drop(lineNo, line, "gag pattern is empty"); continue; }
            try { _ = new Regex(pat); }
            catch (RegexParseException ex) { log.Drop(lineNo, line, "pattern is not a valid regular expression: " + ex.Message); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(pat)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            var cls = Arg(args, 1);

            engine.RemoveRule(pat);
            engine.AddRule(pat, caseSensitive: !caseInsensitive, isEnabled: true, className: cls);
            existing.Add(pat);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Macros ──────────────────────────────────────────────────────────────

    public static ImportResult ImportMacros(string path, MacroEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#macro", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#macro");
            if (args is null || args.Count < 2) { log.Drop(lineNo, line, "not a recognised #macro directive"); continue; }

            var key    = args[0].Trim();
            var action = args[1];
            if (string.IsNullOrEmpty(key)) { log.Drop(lineNo, line, "macro key is empty"); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(key)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            engine.Add(key, action);
            existing.Add(key);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Names ───────────────────────────────────────────────────────────────

    public static ImportResult ImportNames(string path, NameHighlightEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#name", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#name");
            if (args is null || args.Count < 2) { log.Drop(lineNo, line, "not a recognised #name directive"); continue; }

            var (fg, bg) = ParseColorPair(args[0]);
            var name = args[1].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fg)) { log.Drop(lineNo, line, "name or foreground colour is empty"); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(name)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            engine.Add(name, fg, bg);
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Presets ─────────────────────────────────────────────────────────────

    private static readonly Regex PresetPattern = new(
        @"^\s*#preset\s+\{(?<id>[^{}]*)\}\s+\{(?<colors>[^{}]*)\}(?:\s+\{(?<hl>[^{}]*)\})?\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportPresets(string path, PresetEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.ResetToDefaults();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Presets.Keys, StringComparer.OrdinalIgnoreCase);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#preset", StringComparison.OrdinalIgnoreCase)) continue;

            var m = PresetPattern.Match(line);
            if (!m.Success) { log.Drop(lineNo, line, "not a recognised #preset directive"); continue; }

            var id = m.Groups["id"].Value.Trim();
            if (string.IsNullOrEmpty(id)) { log.Drop(lineNo, line, "preset id is empty"); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(id)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            var (fg, bg) = ParseColorPair(m.Groups["colors"].Value);
            bool highlightLine = false;
            if (m.Groups["hl"].Success)
            {
                var hl = m.Groups["hl"].Value.Trim().ToLowerInvariant();
                highlightLine = hl is "true" or "on" or "1" or "yes";
            }

            engine.Apply(new PresetRule
            {
                Id              = id,
                ForegroundColor = fg,
                BackgroundColor = bg,
                HighlightLine   = highlightLine,
            });
            existing.Add(id);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Variables ───────────────────────────────────────────────────────────

    private static readonly Regex VariablePattern = new(
        @"^\s*#var\s+\{(?<name>[^}]*)\}\s+\{(?<value>.*)\}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportVariables(string path, VariableStore store, ImportMode mode)
    {
        if (mode == ImportMode.Replace) store.ClearUserVariables();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(store.GetAll().Keys, StringComparer.OrdinalIgnoreCase);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;

            var m = VariablePattern.Match(line);
            if (!m.Success) { log.Drop(lineNo, line, "not a recognised #var directive"); continue; }

            var name  = m.Groups["name"].Value;
            var value = m.Groups["value"].Value;
            if (string.IsNullOrEmpty(name)) { log.Drop(lineNo, line, "variable name is empty"); continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(name)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            // Set() refuses reserved connection-state names (public #294) —
            // Genie 4's type-flip quirk means any profile that ever ran
            // `#var connected …` carries a stale connected row in
            // variables.cfg; count it as skipped, not imported.
            if (!store.Set(name, value)) { log.Drop(lineNo, line, "the variable store refused this name (reserved connection-state variable)"); continue; }
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Settings ────────────────────────────────────────────────────────────

    /// <summary>
    /// Keys that name a directory. Genie 4 stores these <b>relative to its own
    /// install</b> — the reference <c>settings.cfg</c> has
    /// <c>scriptdir {Scripts}</c>, <c>configdir {Config}</c>,
    /// <c>logdir {Logs}</c> — so applying them would repoint Genie 5's data
    /// root at folders that either don't exist or belong to Genie 4. Refused,
    /// and reported, rather than applied.
    /// </summary>
    private static readonly HashSet<string> DirectorySettings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "scriptdir", "configdir", "logdir", "sounddir",
            "artdir", "mapdir", "plugindir", "reposcriptdir",
        };

    /// <summary>
    /// Genie 4 key → Genie 5 key, where the setting survived under a new name
    /// and the value means the same thing.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedSettings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["maxrowbuffer"] = "scrollbacklines",
            ["rubypath"]     = "lichruby",
            ["licharguments"]= "lichargs",
        };

    /// <summary>
    /// Keys whose Genie 5 counterpart exists but does <b>not</b> take the same
    /// value, so an automatic mapping would be a guess. Reported with the
    /// command to run instead.
    /// </summary>
    private static readonly Dictionary<string, string> AdviseInsteadOfApplying =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["connectstring"]        = "Genie 5 picks the front end by name — set '#config frontend wrayth' instead.",
            ["servertimeout"]        = "Genie 5 has one idle timer — see '#config activitytimeout'.",
            ["servertimeoutcommand"] = "Genie 5 has one idle timer — see '#config activitytimeout'.",
            ["usertimeout"]          = "Genie 5 has one idle timer — see '#config activitytimeout'.",
            ["usertimeoutcommand"]   = "Genie 5 has one idle timer — see '#config activitytimeout'.",
        };

    /// <summary>
    /// Apply a Genie 4 <c>settings.cfg</c> to <paramref name="config"/>, one
    /// key at a time, so the ones Genie 5 cannot honour are reported rather
    /// than silently ignored.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Config.GenieConfig.Load"/>: that applies
    /// whatever it recognises and drops the rest without a word, which for
    /// this file would include repointing the data directories at Genie 4's.
    /// <para>
    /// Settings are app-wide, so <see cref="ImportMode"/> only decides whether
    /// an already-set key may be overwritten — there is nothing to "clear".
    /// </para>
    /// </remarks>
    public static ImportResult ImportSettings(string path, Config.GenieConfig config, ImportMode mode)
    {
        int imported = 0;
        var log = new SkipLog();

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#config", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#config");
            if (args is null || args.Count < 2)
            {
                log.Drop(lineNo, line, "not a recognised #config directive");
                continue;
            }

            var key   = args[0].Trim();
            var value = args[1];

            if (DirectorySettings.Contains(key))
            {
                log.Drop(lineNo, line,
                    $"'{key}' is a Genie 4 folder path — applying it would repoint Genie 5's data directories");
                continue;
            }

            if (AdviseInsteadOfApplying.TryGetValue(key, out var advice))
            {
                log.Drop(lineNo, line, $"'{key}' has no direct equivalent. {advice}");
                continue;
            }

            var target = RenamedSettings.TryGetValue(key, out var renamed) ? renamed : key;

            try
            {
                config.SetSetting(target, value, showException: true);
                imported++;
            }
            catch (Exception ex)
            {
                log.Drop(lineNo, line,
                    target.Equals(key, StringComparison.OrdinalIgnoreCase)
                        ? $"'{key}' is not a Genie 5 setting"
                        : $"'{key}' maps to '{target}', which rejected the value: {ex.Message}");
            }
        }

        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Classes ─────────────────────────────────────────────────────────────

    public static ImportResult ImportClasses(string path, ClassEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0;
        var log = new SkipLog();
        var existing = new HashSet<string>(engine.Names, StringComparer.OrdinalIgnoreCase);

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#class", StringComparison.OrdinalIgnoreCase)) continue;

            var args = DirectiveArgs(line, "#class");
            if (args is null || args.Count < 2) { log.Drop(lineNo, line, "not a recognised #class directive"); continue; }

            var name  = args[0].Trim();
            var state = args[1].Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || name.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                log.Drop(lineNo, line, name.Length == 0 ? "class name is empty" : "the 'default' class is implicit");
                continue;
            }
            if (mode == ImportMode.AddOnly && existing.Contains(name)) { log.ByDesign(lineNo, line, "already present (Add-only mode)"); continue; }

            bool active = state switch
            {
                "true" or "on"  or "yes" or "1" => true,
                "false" or "off" or "no"  or "0" => false,
                _ => true,
            };
            engine.Set(name, active);
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, log.Count) { Skips = log.Items };
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private static (string Fg, string Bg) ParseColorPair(string raw)
    {
        var parts = raw.Split(',', 2);
        var fg = parts[0].Trim();
        var bg = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return (fg, bg);
    }
}
