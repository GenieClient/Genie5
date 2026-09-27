using Genie.Core.Layout;

namespace Genie.Core.Shunts;

/// <summary>Where a shunted line is delivered.</summary>
public enum ShuntSinkKind
{
    /// <summary>The main game window — the shunt did not take the line anywhere
    /// else (a main-window target, a non-text panel, or a closed target).</summary>
    Main,
    /// <summary>A built-in stream window (talk, log, atmospherics, …) —
    /// see <see cref="ShuntDecision.Target"/>.</summary>
    Stream,
    /// <summary>A named script/plugin window, the same kind <c>#echo &gt;Name</c>
    /// creates — see <see cref="ShuntDecision.Target"/>.</summary>
    Window,
}

/// <summary>Resolved delivery for one shunted line.</summary>
/// <param name="Kind">The sink category.</param>
/// <param name="Target">Stream id (lower-case) for <see cref="ShuntSinkKind.Stream"/>,
/// the window name as the rule gave it for <see cref="ShuntSinkKind.Window"/>,
/// null for <see cref="ShuntSinkKind.Main"/>.</param>
public readonly record struct ShuntDecision(ShuntSinkKind Kind, string? Target);

/// <summary>
/// Pure target resolution for a <c>#shunt</c> rule (public #248), kept in Core so
/// the routing matrix is unit-testable without the Avalonia host. The host
/// supplies three facts about its windows; this decides where the line goes.
///
/// <para><b>Names</b> follow the <c>#echo &gt;Window</c> seam: a stream window
/// name lands in that stream's buffer; <c>main</c>/<c>game</c> and the non-text
/// built-in panels (mapper, vitals, experience, …) mean the main window; any
/// other name is a named window, created the first time something is sent to
/// it — exactly what <c>#echo &gt;Kittens</c> does.</para>
///
/// <para><b>A moved line never vanishes.</b> When the target is closed, a stream
/// target follows its own <see cref="WindowSettings.IfClosed"/> redirect chain
/// through <see cref="IfClosedResolver"/> (so a shunt to a closed Talk window
/// lands wherever Talk's own text would), and everything else falls back to the
/// main window. The one deliberate departure from the resolver: its
/// <see cref="IfClosedSinkKind.Drop"/> (an IfClosed of <c>""</c>, which DR itself
/// declares for OOC) resolves to Main here. That setting says what to do with
/// the stream's OWN overflow; a shunt is the user asking to see a line
/// somewhere, and dropping it would turn a routing rule into a silent gag.</para>
/// </summary>
public static class ShuntRouter
{
    /// <param name="window">The rule's target window name.</param>
    /// <param name="isStream">Is this (lower-cased) name a stream window the host
    /// has a buffer for?</param>
    /// <param name="isReserved">Is this name a built-in panel that is not a
    /// text sink (mapper, vitals, …)? Main/game are handled here, not by the host.</param>
    /// <param name="isOpen">Would a line sent to this window be seen? For a stream,
    /// its panel is open. For a named window, it is open OR does not exist yet
    /// (first use creates and shows it, as <c>#echo &gt;Name</c> does).</param>
    /// <param name="store">The per-window settings store, for a closed stream's
    /// IfClosed chain. Null = no chain; a closed stream goes to Main.</param>
    public static ShuntDecision Resolve(
        string                window,
        Func<string, bool>    isStream,
        Func<string, bool>    isReserved,
        Func<string, bool>    isOpen,
        WindowSettingsStore?  store)
    {
        var name = (window ?? "").Trim();
        if (name.Length == 0 || IsMainName(name))
            return new(ShuntSinkKind.Main, null);

        var id = name.ToLowerInvariant();
        if (isStream(id))
        {
            if (isOpen(id)) return new(ShuntSinkKind.Stream, id);
            if (store is null) return new(ShuntSinkKind.Main, null);

            var decision = IfClosedResolver.Resolve(id, store, isOpen);
            return decision.Kind == IfClosedSinkKind.Stream && decision.StreamId is { } redirect
                   && isStream(redirect.ToLowerInvariant())
                ? new(ShuntSinkKind.Stream, redirect.ToLowerInvariant())
                : new(ShuntSinkKind.Main, null);          // Main, Drop (see remarks), or no buffer
        }

        if (isReserved(name))
            return new(ShuntSinkKind.Main, null);          // non-text panel: nowhere to append

        return isOpen(name)
            ? new(ShuntSinkKind.Window, name)
            : new(ShuntSinkKind.Main, null);               // user closed it: keep the line visible
    }

    private static bool IsMainName(string name) =>
        name.Equals("main", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("game", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(IfClosedResolver.MainWindowId, StringComparison.OrdinalIgnoreCase);
}
