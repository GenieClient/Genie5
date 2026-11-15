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

    /// <summary>Put the resolved font in the application resources. It is
    /// resolved once; later calls only put it back if it's missing. App.axaml
    /// holds the unresolved chain as the default, so the boxes keep it if this
    /// can't run.</summary>
    public static void EnsureRegistered()
    {
        if (Application.Current is not { } app) return;
        try
        {
            _resolved ??= new FontFamily(Resolve(Chain, FontManager.Current.SystemFonts.Select(f => f.Name)));
            if (!app.Resources.TryGetValue(ResourceKey, out var current) || !ReferenceEquals(current, _resolved))
                app.Resources[ResourceKey] = _resolved;
        }
        catch (Exception ex)
        {
            Diagnostics.ErrorLog.Log("EditorFonts.EnsureRegistered", ex);
        }
    }
}
