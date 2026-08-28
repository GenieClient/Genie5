using System.Text.RegularExpressions;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
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

/// <summary>Per-file counts returned by each <c>ImportX</c> method.</summary>
public readonly record struct ImportResult(int Imported, int Skipped);

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

    /// <summary>Config types the caller selected that had no matching file on disk.</summary>
    public List<string> MissingFiles { get; } = new();
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
    All         = Aliases | Triggers | Highlights | Substitutes | Gags
                | Macros  | Names    | Presets    | Variables   | Classes,
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

    // ── Aliases ─────────────────────────────────────────────────────────────

    private static readonly Regex AliasPattern = new(
        @"^\s*#alias(?:\s+(?<verb>add|delete))?\s+\{(?<name>[^}]*)\}\s+\{(?<expansion>.*)\}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportAliases(string path, AliasEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Aliases.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#alias", StringComparison.OrdinalIgnoreCase)) continue;

            var m = AliasPattern.Match(line);
            if (!m.Success) { skipped++; continue; }
            if (m.Groups["verb"].Value.Equals("delete", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

            var name      = m.Groups["name"].Value;
            var expansion = m.Groups["expansion"].Value;
            if (string.IsNullOrEmpty(name)) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(name)) { skipped++; continue; }

            engine.RemoveAlias(name);
            engine.AddAlias(name, expansion, isEnabled: true);
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Triggers ────────────────────────────────────────────────────────────

    private static readonly Regex TriggerPattern = new(
        @"^\s*#trigger\s+\{(?<pat>[^{}]*)\}\s+\{(?<action>[^{}]*)\}(?:\s+\{(?<cls>[^{}]*)\})?\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportTriggers(string path, TriggerEngineFinal engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Triggers.Select(t => t.Pattern), StringComparer.Ordinal);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#trigger", StringComparison.OrdinalIgnoreCase)) continue;

            var m = TriggerPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var pat = m.Groups["pat"].Value;
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

            if (string.IsNullOrEmpty(pat)) { skipped++; continue; }
            try { _ = new Regex(pat); }
            catch (RegexParseException) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(pat)) { skipped++; continue; }

            var action = m.Groups["action"].Value;
            var cls    = m.Groups["cls"].Success ? m.Groups["cls"].Value : string.Empty;

            engine.RemoveTrigger(pat);
            engine.AddTrigger(pat, action, caseSensitive: !caseInsensitive, isEnabled: true, className: cls);
            existing.Add(pat);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Highlights ──────────────────────────────────────────────────────────

    private static readonly Regex HighlightPattern = new(
        @"^\s*#highlight\s+\{(?<type>[^{}]*)\}\s+\{(?<colors>[^{}]*)\}\s+\{(?<pattern>[^{}]*)\}(?:\s+\{(?<cls>[^{}]*)\})?(?:\s+\{[^{}]*\})?\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportHighlights(string path, HighlightEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Pattern), StringComparer.Ordinal);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#highlight", StringComparison.OrdinalIgnoreCase)) continue;

            var m = HighlightPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var matchType = ParseMatchType(m.Groups["type"].Value);
            if (matchType is null) { skipped++; continue; }

            var (fg, bg)    = ParseColorPair(m.Groups["colors"].Value);
            var rulePattern = m.Groups["pattern"].Value;
            if (string.IsNullOrEmpty(rulePattern) || string.IsNullOrEmpty(fg)) { skipped++; continue; }

            if (matchType == HighlightMatchType.Regex)
            {
                try { _ = new Regex(rulePattern); }
                catch (RegexParseException) { skipped++; continue; }
            }

            if (mode == ImportMode.AddOnly && existing.Contains(rulePattern)) { skipped++; continue; }

            var cls = m.Groups["cls"].Success ? m.Groups["cls"].Value : string.Empty;

            engine.RemoveRule(rulePattern);
            engine.AddRule(rulePattern, fg, bg, matchType.Value, caseSensitive: false, isEnabled: true, className: cls);
            existing.Add(rulePattern);
            imported++;
        }
        return new ImportResult(imported, skipped);
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

    private static readonly Regex SubsPattern = new(
        @"^\s*#subs\s+\{(?<pat>[^{}]*)\}\s+\{(?<repl>[^{}]*)\}(?:\s+\{(?<cls>[^{}]*)\})?\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportSubstitutes(string path, SubstituteEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Pattern), StringComparer.Ordinal);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#subs", StringComparison.OrdinalIgnoreCase)) continue;

            var m = SubsPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var pat = m.Groups["pat"].Value;
            bool caseInsensitive = false;
            if (pat.StartsWith('/')) pat = pat[1..];
            if (pat.EndsWith("/i", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; pat = pat[..^2]; }
            else if (pat.EndsWith('/')) pat = pat[..^1];

            if (string.IsNullOrEmpty(pat)) { skipped++; continue; }
            try { _ = new Regex(pat); }
            catch (RegexParseException) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(pat)) { skipped++; continue; }

            var repl = m.Groups["repl"].Value;
            var cls  = m.Groups["cls"].Success ? m.Groups["cls"].Value : string.Empty;

            engine.RemoveRule(pat);
            engine.AddRule(pat, repl, caseSensitive: !caseInsensitive, isEnabled: true, className: cls);
            existing.Add(pat);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Gags ────────────────────────────────────────────────────────────────

    private static readonly Regex GagPattern = new(
        @"^\s*#gag\s+\{(?<pat>[^{}]*)\}(?:\s+\{(?<cls>[^{}]*)\})?\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportGags(string path, GagEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Pattern), StringComparer.Ordinal);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#gag", StringComparison.OrdinalIgnoreCase)) continue;

            var m = GagPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var pat = m.Groups["pat"].Value;
            bool caseInsensitive = false;
            if (pat.StartsWith('/')) pat = pat[1..];
            if (pat.EndsWith("/i", StringComparison.OrdinalIgnoreCase)) { caseInsensitive = true; pat = pat[..^2]; }
            else if (pat.EndsWith('/')) pat = pat[..^1];

            if (string.IsNullOrEmpty(pat)) { skipped++; continue; }
            try { _ = new Regex(pat); }
            catch (RegexParseException) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(pat)) { skipped++; continue; }

            var cls = m.Groups["cls"].Success ? m.Groups["cls"].Value : string.Empty;

            engine.RemoveRule(pat);
            engine.AddRule(pat, caseSensitive: !caseInsensitive, isEnabled: true, className: cls);
            existing.Add(pat);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Macros ──────────────────────────────────────────────────────────────

    private static readonly Regex MacroPattern = new(
        @"^\s*#macro\s+\{(?<key>[^{}]*)\}\s+\{(?<action>[^{}]*)\}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportMacros(string path, MacroEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#macro", StringComparison.OrdinalIgnoreCase)) continue;

            var m = MacroPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var key    = m.Groups["key"].Value.Trim();
            var action = m.Groups["action"].Value;
            if (string.IsNullOrEmpty(key)) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(key)) { skipped++; continue; }

            engine.Add(key, action);
            existing.Add(key);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Names ───────────────────────────────────────────────────────────────

    private static readonly Regex NamePattern = new(
        @"^\s*#name\s+\{(?<colors>[^{}]*)\}\s+\{(?<name>[^{}]*)\}(?:\s+\{[^{}]*\}){0,2}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportNames(string path, NameHighlightEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Rules.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#name", StringComparison.OrdinalIgnoreCase)) continue;

            var m = NamePattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var (fg, bg) = ParseColorPair(m.Groups["colors"].Value);
            var name = m.Groups["name"].Value.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fg)) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(name)) { skipped++; continue; }

            engine.Add(name, fg, bg);
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Presets ─────────────────────────────────────────────────────────────

    private static readonly Regex PresetPattern = new(
        @"^\s*#preset\s+\{(?<id>[^{}]*)\}\s+\{(?<colors>[^{}]*)\}(?:\s+\{(?<hl>[^{}]*)\})?\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportPresets(string path, PresetEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.ResetToDefaults();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Presets.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#preset", StringComparison.OrdinalIgnoreCase)) continue;

            var m = PresetPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var id = m.Groups["id"].Value.Trim();
            if (string.IsNullOrEmpty(id)) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(id)) { skipped++; continue; }

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
        return new ImportResult(imported, skipped);
    }

    // ── Variables ───────────────────────────────────────────────────────────

    private static readonly Regex VariablePattern = new(
        @"^\s*#var\s+\{(?<name>[^}]*)\}\s+\{(?<value>.*)\}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportVariables(string path, VariableStore store, ImportMode mode)
    {
        if (mode == ImportMode.Replace) store.ClearUserVariables();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(store.GetAll().Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;

            var m = VariablePattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var name  = m.Groups["name"].Value;
            var value = m.Groups["value"].Value;
            if (string.IsNullOrEmpty(name)) { skipped++; continue; }

            if (mode == ImportMode.AddOnly && existing.Contains(name)) { skipped++; continue; }

            // Set() refuses reserved connection-state names (public #294) —
            // Genie 4's type-flip quirk means any profile that ever ran
            // `#var connected …` carries a stale connected row in
            // variables.cfg; count it as skipped, not imported.
            if (!store.Set(name, value)) { skipped++; continue; }
            existing.Add(name);
            imported++;
        }
        return new ImportResult(imported, skipped);
    }

    // ── Classes ─────────────────────────────────────────────────────────────

    private static readonly Regex ClassPattern = new(
        @"^\s*#class\s+\{(?<name>[^{}]*)\}\s+\{(?<state>[^{}]*)\}\s*$",
        RegexOptions.IgnoreCase);

    public static ImportResult ImportClasses(string path, ClassEngine engine, ImportMode mode)
    {
        if (mode == ImportMode.Replace) engine.Clear();

        int imported = 0, skipped = 0;
        var existing = new HashSet<string>(engine.Names, StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#!")) continue;
            if (!line.StartsWith("#class", StringComparison.OrdinalIgnoreCase)) continue;

            var m = ClassPattern.Match(line);
            if (!m.Success) { skipped++; continue; }

            var name  = m.Groups["name"].Value.Trim();
            var state = m.Groups["state"].Value.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || name.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            if (mode == ImportMode.AddOnly && existing.Contains(name)) { skipped++; continue; }

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
        return new ImportResult(imported, skipped);
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
