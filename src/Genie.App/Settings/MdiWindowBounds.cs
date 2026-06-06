namespace Genie.App.Settings;

/// <summary>
/// One windowed-mode (MDI) child window's saved geometry, keyed by the
/// dockable's Id. Coordinates are in Dock's MDI coordinate space (the same
/// values Dock writes to <c>IMdiDocument.MdiBounds</c>).
/// <para>
/// Persisted only as part of a <see cref="SavedLayout"/> (<see
/// cref="SavedLayout.MdiBounds"/>); it is also held transiently in memory
/// across a within-session mode toggle. There is no standalone on-disk store —
/// windowed geometry never auto-persists across restarts on its own.
/// </para>
/// </summary>
public sealed record MdiWindowBounds(
    double X, double Y, double Width, double Height, string State);
