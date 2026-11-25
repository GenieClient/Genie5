using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace Genie.App.Controls;

/// <summary>
/// The monospace font of the Configuration dialog's pattern boxes (public #402).
/// They asked for <c>Consolas,Courier New,monospace</c> on every box, so on a
/// machine without Consolas each one went looking for it again. The chain is now
/// resolved once, to the first family that is installed, and shared as the
/// <see cref="ResourceKey"/> resource. Which font you see doesn't change: it is
/// the same family the chain fell back to before.
/// </summary>
public static class EditorFonts
{
    public const string ResourceKey = "Genie.EditorMonoFont";
    public const string Chain       = "Consolas,Courier New,monospace";

    /// <summary>The other fixed font lists in the UI and the resources that
    /// carry them (public #393). They used to be written out on each control;
    /// on Linux, where most of these families are missing, every such control
    /// sent Avalonia back to load fallback font data. Each list is resolved
    /// once by <see cref="FontChain"/> to the families that are installed, in
    /// the same order, so the font you see is the one the list already fell
    /// back to. The defaults in App.axaml must stay in step with this table.</summary>
    public static readonly IReadOnlyList<(string Key, string Chain)> SharedChains = new[]
    {
        ("Genie.UiMonoFont", "Consolas,Menlo,monospace"),
        ("Genie.CodeFont",   "Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
        ("Genie.TitleFont",  "Verdana,Segoe UI,sans-serif"),
    };

    private static FontFamily? _resolved;

    /// <summary>The first name in <paramref name="chain"/> that is in
    /// <paramref name="installed"/> (ignoring case), else the chain's last name
    /// (the generic family the platform maps itself, e.g. <c>monospace</c>).</summary>
    public static string Resolve(string chain, IEnumerable<string> installed)
    {
        var names = chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return chain;
        var have = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(have.Contains) ?? names[^1];
    }

    /// <summary>Put the resolved fonts in the application resources. Each is
    /// resolved once; later calls only put it back if it's missing. App.axaml
    /// holds the unresolved chains as the defaults, so controls keep those if
    /// this can't run. Called at start-up (#393) and again when the
    /// Configuration dialog opens (#402).</summary>
    public static void EnsureRegistered()
    {
        if (Application.Current is not { } app) return;
        try
        {
            _resolved ??= new FontFamily(Resolve(Chain, FontManager.Current.SystemFonts.Select(f => f.Name)));
            if (!app.Resources.TryGetValue(ResourceKey, out var current) || !ReferenceEquals(current, _resolved))
                app.Resources[ResourceKey] = _resolved;

            foreach (var (key, chain) in SharedChains)
            {
                var family = FontChain.Resolve(chain);   // cached per chain: the same instance each call
                if (!app.Resources.TryGetValue(key, out var have) || !ReferenceEquals(have, family))
                    app.Resources[key] = family;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.ErrorLog.Log("EditorFonts.EnsureRegistered", ex);
        }
    }
}
