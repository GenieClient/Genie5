namespace Genie.Core.Presets;

public sealed class PresetEngine
{
    private readonly Dictionary<string, PresetRule> _presets = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, PresetRule> Presets => _presets;

    public PresetEngine() { SetDefaults(); }

    /// <summary>
    /// Genie 4 / DR-Wrayth default preset palette. IDs match what the parser
    /// and AutoMapper emit so user themes can override per-token colours.
    /// </summary>
    public void SetDefaults()
    {
        // ── Game text streams ───────────────────────────────────────────────
        Set("roomdesc",         "Silver");
        Set("roomname",         "#FF8000");
        Set("speech",           "Lime");
        Set("whispers",         "Magenta");
        Set("thoughts",         "Cyan");
        Set("creatures",        "Gold");      // MonsterBold (#131) — bright gold, Wrayth/Genie 3-4 look
        Set("familiar",         "PaleGreen");
        Set("inputuser",        "Yellow");
        Set("inputother",       "GreenYellow");
        Set("scriptecho",       "Cyan");

        // ── Status bars ─────────────────────────────────────────────────────
        Set("health",           "Red",      "#400000");
        Set("mana",             "Aqua",     "#000040");
        Set("stamina",          "Green",    "#004000");
        Set("spirit",           "Purple",   "#400040");
        Set("concentration",    "White",    "#000040");
        Set("roundtime",        "Aqua",     "#00004B");
        Set("castbar",          "Magenta");

        // ── AutoMapper overlay ──────────────────────────────────────────────
        Set("automapper.line",      "Black", "White");
        Set("automapper.lineclimb", "Green", "White");
        Set("automapper.linego",    "Blue",  "White");
        Set("automapper.linestump", "Cyan",  "White");
        Set("automapper.node",      "White", "White");
        Set("automapper.panel",     "Black", "PaleGoldenrod");
        Set("automapper.path",      "Green", "LightGreen");

        // ── UI chrome (Wrayth-style tokens, used by chrome themes) ──────────
        Set("ui.button",         "Black",     "Silver");
        Set("ui.menu",           "Black",     "#EEEEEE");
        Set("ui.menu.checked",   "LightBlue");
        Set("ui.menu.highlight", "LightBlue");
        Set("ui.status",         "Black",     "#EEEEEE");
        Set("ui.textbox",        "Black",     "White");
        Set("ui.window",         "Black",     "#EEEEEE");
    }

    private void Set(string id, string fg, string bg = "", bool highlightLine = false)
        => _presets[id] = new PresetRule
        {
            Id = id, ForegroundColor = fg, BackgroundColor = bg, HighlightLine = highlightLine,
            // Seeded defaults are app-wide: tag Global so a #257 split-save
            // writes them to the shared file, not into every profile.
            Scope = Persistence.RuleScope.Global,
        };

    public void Apply(PresetRule rule)      => _presets[rule.Id] = rule;
    public void ResetToDefaults()           { _presets.Clear(); SetDefaults(); }
    public PresetRule? Get(string id)       => _presets.TryGetValue(id, out var r) ? r : null;
    public string GetForeground(string id)  => _presets.TryGetValue(id, out var r) ? r.ForegroundColor : "Default";
    public string GetBackground(string id)  => _presets.TryGetValue(id, out var r) ? r.BackgroundColor : string.Empty;
    public bool GetHighlightLine(string id) => _presets.TryGetValue(id, out var r) && r.HighlightLine;
}
