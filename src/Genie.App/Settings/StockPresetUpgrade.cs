using System.IO;
using Genie.Core.Persistence;
using Genie.Core.Presets;

namespace Genie.App.Settings;

/// <summary>
/// One-time upgrade of preset colours that Genie saved without the user choosing
/// them (public #406). Genie 5 writes every preset to <c>presets.json</c>, stock
/// values included, so a stock colour a release has since changed stays on disk
/// and wins over the new default. An entry still holding exactly a previous stock
/// value is replaced; anything the user picked is left alone. Files that can't be
/// read are skipped, never rewritten.
/// </summary>
public static class StockPresetUpgrade
{
    /// <summary>The vitals bar presets as Genie 5 shipped them before they
    /// coloured the bars. Nothing read them then, so nobody chose them.</summary>
    public static readonly IReadOnlyDictionary<string, (string Fg, string Bg)> OldVitals =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["health"]        = ("Red",    "#400000"),
            ["mana"]          = ("Aqua",   "#000040"),
            ["stamina"]       = ("Green",  "#004000"),
            ["spirit"]        = ("Purple", "#400040"),
            ["concentration"] = ("White",  "#000040"),
        };

    /// <summary>Every config folder that can hold a <c>presets.json</c>: the shared
    /// one, each saved profile's, and every character folder under
    /// <c>Profiles/</c> on disk (a character reached with <c>#connect</c> and never
    /// saved as a profile still has one).</summary>
    public static IEnumerable<string> ConfigDirs(string sharedConfigDir, IEnumerable<string> savedProfileDirs)
    {
        var dirs = new List<string> { sharedConfigDir };
        dirs.AddRange(savedProfileDirs);
        try
        {
            var profilesRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sharedConfigDir))!, "Profiles");
            if (Directory.Exists(profilesRoot))
                dirs.AddRange(Directory.EnumerateDirectories(profilesRoot));
        }
        catch { /* unreadable data folder: the listed dirs still get upgraded */ }
        return dirs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Replace entries in each folder's <c>presets.json</c> that still
    /// hold <paramref name="oldStock"/> with <paramref name="replacement"/>.
    /// Returns how many files changed.</summary>
    public static int Run(IEnumerable<string> configDirs,
                          IReadOnlyDictionary<string, (string Fg, string Bg)> oldStock,
                          Func<string, (string Fg, string Bg)> replacement)
    {
        var p = new PersistenceService();
        int files = 0;
        foreach (var path in configDirs.Select(d => Path.Combine(d, "presets.json")).Where(File.Exists))
        {
            try
            {
                if (!p.TryLoadPresets(path, out var models, out _)) continue;   // never rewrite an unreadable file
                var changed = false;
                foreach (var m in models)
                {
                    if (!oldStock.TryGetValue(m.Id, out var old)) continue;
                    if (!Same(m.ForegroundColor, old.Fg) || !Same(m.BackgroundColor, old.Bg)) continue;
                    (m.ForegroundColor, m.BackgroundColor) = replacement(m.Id);
                    changed = true;
                }
                if (!changed) continue;
                RuleFileWatcher.MarkAppWrite(path);
                p.SavePresets(path, models.Select(m => new PresetRule
                {
                    Id = m.Id, ForegroundColor = m.ForegroundColor,
                    BackgroundColor = m.BackgroundColor, HighlightLine = m.HighlightLine,
                }));
                files++;
            }
            catch { /* best effort: a file that can't be written keeps its values */ }
        }
        return files;
    }

    /// <summary>The stock value <paramref name="id"/> has today.</summary>
    public static (string Fg, string Bg) CurrentStock(string id)
    {
        var stock = new PresetEngine();
        return (stock.GetForeground(id), stock.GetBackground(id));
    }

    private static bool Same(string? a, string? b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
