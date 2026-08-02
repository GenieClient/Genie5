namespace Genie.Core.Layout;

/// <summary>Where a closed stream panel's text should be delivered.</summary>
public enum IfClosedSinkKind
{
    /// <summary>The main game window.</summary>
    Main,
    /// <summary>Discard the line (the user disabled the fallback).</summary>
    Drop,
    /// <summary>Deliver into another stream window (see <see cref="IfClosedDecision.StreamId"/>).</summary>
    Stream,
}

/// <summary>Resolved routing target for a closed stream panel.</summary>
/// <param name="Kind">The sink category.</param>
/// <param name="StreamId">The target window id when <see cref="Kind"/> is
/// <see cref="IfClosedSinkKind.Stream"/>; otherwise <c>null</c>.</param>
public readonly record struct IfClosedDecision(IfClosedSinkKind Kind, string? StreamId);

/// <summary>
/// Pure resolver for <see cref="WindowSettings.IfClosed"/> (public #211). Given a
/// stream whose own panel is closed, decides where its text goes by walking the
/// per-window <c>IfClosed</c> setting.
///
/// <para><b>Sentinels</b> (matching the rest of the Option-A settings model):
/// <c>null</c> = default → main game window; <c>""</c> = disabled → drop the
/// line. Any other value names a target window id.</para>
///
/// <para><b>Chain following.</b> When the named target is itself a <i>closed</i>
/// window, we follow <b>its</b> <c>IfClosed</c> in turn (talk → log → …). A
/// <see cref="System.Collections.Generic.HashSet{T}"/> of visited ids caps the
/// walk, so a user-configured cycle (or self-reference) falls back to Main
/// instead of spinning the UI thread. This is a deliberate enhancement over
/// literal Genie 4 (single redirect): a redirect into a hidden window would
/// otherwise be silently invisible.</para>
///
/// <para><b>Never silently drops.</b> An unknown / unregistered target id
/// resolves to <see cref="IfClosedSinkKind.Main"/>, never
/// <see cref="IfClosedSinkKind.Drop"/> — the namespace-rot failure mode from
/// #211, where a dangling id (<c>"main"</c>, <c>"conversation"</c>) would have
/// dropped combat/talk/whispers text. Only an explicit <c>""</c> drops.</para>
///
/// <para>Kept in <c>Genie.Core</c> as a pure function of (stream id, settings
/// store, visibility predicate) so the chain/cycle logic is unit-testable —
/// <c>Genie.App</c> has no test project.</para>
/// </summary>
public static class IfClosedResolver
{
    /// <summary>Registered id of the main game window ("Game"). Accepted target
    /// meaning "route to Main".</summary>
    public const string MainWindowId = "game-text";

    /// <summary>Legacy / server alias for <see cref="MainWindowId"/>. DR declares
    /// some streams <c>ifClosed="main"</c>, and older profiles may have persisted
    /// <c>"main"</c>; both resolve to Main with no migration pass.</summary>
    public const string MainWindowAlias = "main";

    /// <summary>
    /// Resolve the delivery target for <paramref name="streamId"/>, whose own
    /// panel is assumed closed by the caller.
    /// </summary>
    /// <param name="streamId">The closed stream's window id (e.g. "talk").</param>
    /// <param name="store">The live per-window settings store.</param>
    /// <param name="isVisible">Predicate: is the window with this id currently
    /// open? Used to decide whether to deliver to a target or follow its chain.</param>
    public static IfClosedDecision Resolve(
        string streamId,
        WindowSettingsStore store,
        Func<string, bool> isVisible)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = streamId;

        // Loop terminates in at most (window-count) hops: every iteration either
        // returns, or advances to a not-yet-visited id. A repeat id (cycle or
        // self-reference) fails the Add and drops out to the Main fallback below.
        while (visited.Add(current))
        {
            var raw = store.Get(current).IfClosed;

            if (raw is null)                                   // default
                return new(IfClosedSinkKind.Main, null);
            if (raw.Length == 0)                               // "" = disabled
                return new(IfClosedSinkKind.Drop, null);
            if (string.Equals(raw, MainWindowId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, MainWindowAlias, StringComparison.OrdinalIgnoreCase))
                return new(IfClosedSinkKind.Main, null);

            // Anti-rot safety: an unregistered target NEVER drops — goes to Main.
            if (!store.All.ContainsKey(raw))
                return new(IfClosedSinkKind.Main, null);

            if (isVisible(raw))                                // target open → deliver
                return new(IfClosedSinkKind.Stream, raw);

            current = raw;                                     // target closed → follow chain
        }

        return new(IfClosedSinkKind.Main, null);               // cycle → Main
    }
}
