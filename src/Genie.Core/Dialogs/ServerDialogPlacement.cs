namespace Genie.Core.Dialogs;

/// <summary>Where a dialog goes the first time it is shown.</summary>
public enum ServerDialogPlacementKind
{
    /// <summary>Docked in the right-hand column — the default, and what every
    /// dialog did before placement hints were read.</summary>
    DockRight,
    /// <summary>Docked in the left-hand column.</summary>
    DockLeft,
    /// <summary>A floating window centred over the main window, sized from the
    /// server's width/height. Where the user then moves it sticks.</summary>
    Float,
    /// <summary>Like <see cref="Float"/>, but re-centred on EVERY open: DR's
    /// <c>force-center</c>, used for confirmation-style dialogs
    /// (<c>bank_debt</c>) that should not come back wherever they were last
    /// dragged.</summary>
    FloatAlwaysCentered,
    /// <summary>A tab in the same group as another window, named by
    /// <see cref="ServerDialogPlacement.Target"/> — the "Existing window"
    /// answer. Not a DR hint; the user picks the window.</summary>
    WithWindow,
}

/// <summary>A resolved placement plus the server's size hint, in pixels.</summary>
public sealed record ServerDialogPlacement(ServerDialogPlacementKind Kind, int? Width, int? Height,
                                           string? Target = null)
{
    public static readonly ServerDialogPlacement Default = new(ServerDialogPlacementKind.DockRight, null, null);

    /// <summary>Alongside the window with dock id <paramref name="target"/>
    /// (<see cref="ServerDialogMode.ExistingWindow"/>). No target falls back to
    /// the default rather than guessing one.</summary>
    public static ServerDialogPlacement With(string? target) =>
        string.IsNullOrWhiteSpace(target)
            ? Default
            : new(ServerDialogPlacementKind.WithWindow, null, null, target.Trim());

    public bool Floats => Kind is ServerDialogPlacementKind.Float
                               or ServerDialogPlacementKind.FloatAlwaysCentered;

    /// <summary>
    /// Read the server's <c>openDialog</c> hints (#156, "Where DR proposes").
    /// Values seen in the wild (dialog journal + recordings, 2026-09):
    /// <c>right</c> (injuries, befriend), <c>center</c> (spellChoose,
    /// injuries-&lt;charnum&gt;), <c>force-center</c> (bank_debt); the Genie 4
    /// DynamicWindows plugin also handled <c>left</c> and <c>detach</c>.
    /// Anything else — including no hint — docks right, as before.
    /// (<c>statBar</c> and <c>quickBar</c> never reach here: minivitals is
    /// excluded and quick-bar dialogs default to Ignore.)
    /// </summary>
    public static ServerDialogPlacement From(string? location, string? width, string? height)
    {
        var kind = (location ?? "").Trim().ToLowerInvariant() switch
        {
            "left"         => ServerDialogPlacementKind.DockLeft,
            "center"       => ServerDialogPlacementKind.Float,
            "detach"       => ServerDialogPlacementKind.Float,
            "force-center" => ServerDialogPlacementKind.FloatAlwaysCentered,
            _              => ServerDialogPlacementKind.DockRight,
        };
        return new(kind, Size(width), Size(height));
    }

    /// <summary>A plain positive pixel count, or null. Percentages and junk are
    /// ignored rather than guessed at — the float then keeps a default size.</summary>
    private static int? Size(string? raw) =>
        int.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : null;
}
