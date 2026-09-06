using Genie.Core.Events;

namespace Genie.Core.Dialogs;

/// <summary>
/// One control placed on the grid inferred by <see cref="DialogGridLayout"/>.
/// <see cref="OriginalTop"/> is the raw signed <c>top</c> the server sent
/// (negative = measured from the bottom), kept so a renderer can break ties
/// the inference deliberately does not.
/// </summary>
public sealed record DialogGridCell(
    DialogControl Control,
    int  Column,
    int  Row,
    bool FullWidth,
    bool CentreAligned,
    bool RightAligned,
    bool BottomAnchored,
    int  OriginalTop)
{
    public string Id => Control.Id;
}

/// <summary>
/// The inferred layout of one dialog: cells split into the three bands a
/// renderer lays out in order — regular <see cref="Body"/> rows, then
/// <see cref="CentreBody"/>, then <see cref="Bottom"/> (the bottom-anchored
/// button strip). <see cref="Columns"/>/<see cref="Rows"/> size the body grid
/// only; centre and bottom cells sit outside it.
/// </summary>
public sealed class DialogGrid
{
    public static readonly DialogGrid Empty = new(Array.Empty<DialogGridCell>());

    public DialogGrid(IReadOnlyList<DialogGridCell> cells)
    {
        Cells      = cells;
        Body       = cells.Where(c => !c.BottomAnchored && !c.CentreAligned)
                          .OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
        CentreBody = cells.Where(c => c.CentreAligned && !c.BottomAnchored)
                          .OrderBy(c => c.Row).ThenBy(c => c.Column).ToList();
        Bottom     = cells.Where(c => c.BottomAnchored).ToList();
        Columns    = Body.Count > 0 ? Body.Max(c => c.Column) + 1 : 0;
        Rows       = Body.Count > 0 ? Body.Max(c => c.Row)    + 1 : 0;
    }

    public IReadOnlyList<DialogGridCell> Cells      { get; }
    public IReadOnlyList<DialogGridCell> Body       { get; }
    public IReadOnlyList<DialogGridCell> CentreBody { get; }
    public IReadOnlyList<DialogGridCell> Bottom     { get; }
    public int Columns { get; }
    public int Rows    { get; }

    public DialogGridCell? this[string id] =>
        Cells.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Turns the server's absolute <c>left</c>/<c>top</c> pixel hints into a
/// logical column/row grid (#156 Phase 1).
///
/// <para>DR describes dialogs as absolutely-positioned controls measured
/// against Wrayth's fonts and metrics. Rendering those coordinates literally
/// does not survive a different font stack, so the maintained Genie 4
/// DynamicWindows plugin (Thires, used here with his permission — clean-room
/// reimplementation, not a copy) instead snaps the coordinates into bands and
/// lays the result out on a grid, letting the toolkit size each control to its
/// content. This is a port of that inference. The measurement half of the
/// original is not needed: an Avalonia <c>Grid</c> with <c>Auto</c> rows and
/// columns sizes to content natively, where WinForms would not.</para>
///
/// <para>Pure and UI-free by design — the renderer lives in Genie.App, so this
/// stays testable offline against captured <c>dialogData</c> blocks.</para>
///
/// <para>Deliberate deviations from the Genie 4 original, all widening:
/// percent geometry (<c>left='20%'</c>) contributes its numeric part instead of
/// collapsing to zero; <see cref="DialogGridCell.OriginalTop"/> keeps the raw
/// signed value rather than the original's mixed encoding; controls with no
/// geometry at all are flowed after the grid instead of dropped; two
/// full-width rules are tightened so side-by-side panels survive (see the
/// comments at each); and the per-dialog pixel nudges the original carries for
/// its store and bug-report windows are NOT ported — if those need help they
/// belong in the Phase 2 bespoke registry, not in the generic pass.</para>
/// </summary>
public static class DialogGridLayout
{
    /// <summary>Controls whose <c>top</c> differs by at most this share a row.</summary>
    public const int RowSnap = 10;

    /// <summary>Controls whose <c>left</c> differs by at most this share a column.</summary>
    public const int LeftSnap = 20;

    public static DialogGrid Infer(IReadOnlyList<DialogControl>? controls)
    {
        if (controls is null || controls.Count == 0) return DialogGrid.Empty;

        // clearContainer is a reset marker, never a rendered control.
        var elems = controls.Where(c => c.Type != DialogControlType.ClearContainer).ToList();
        if (elems.Count == 0) return DialogGrid.Empty;

        var placed = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
        var order  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < elems.Count; i++) order.TryAdd(elems[i].Id, i);

        // ── Step 1: snap `top` values into row bands ──────────────────────────
        // A band keeps the FIRST top it saw as its representative rather than a
        // running centroid — matching the original, and stable against the order
        // controls arrive in. align="center" controls never merge with
        // non-centred ones: they take their own row even when the tops are
        // within the snap distance.
        var bands = new List<(int Top, List<DialogControl> Elems)>();
        foreach (var e in elems)
        {
            if (IsBottomAnchored(e)) continue;
            if (!TryCoord(e.Top, out int top)) continue;   // no usable `top` at all
            top = NormaliseTop(top);
            bool centre = IsCentred(e);

            var hit = bands.FirstOrDefault(b =>
                Math.Abs(b.Top - top) <= RowSnap && b.Elems.All(x => IsCentred(x) == centre));

            if (hit.Elems is not null) hit.Elems.Add(e);
            else bands.Add((top, new List<DialogControl> { e }));
        }
        bands.Sort((a, b) => a.Top.CompareTo(b.Top));

        var topToRow = new Dictionary<int, int>();
        for (int i = 0; i < bands.Count; i++) topToRow[bands[i].Top] = i;

        // Only for controls placed by their OWN top (anchored ones in Step 4);
        // a band's own members use their band index directly, because scanning
        // by distance here cannot honour the centred/non-centred split and would
        // pull a control back onto the row that split was there to avoid.
        // Nearest band wins, not merely the first within range.
        int RowForTop(int top)
        {
            top = NormaliseTop(top);
            int best = -1, bestDelta = int.MaxValue;
            foreach (var b in bands)
            {
                int delta = Math.Abs(b.Top - top);
                if (delta <= RowSnap && delta < bestDelta) { best = topToRow[b.Top]; bestDelta = delta; }
            }
            return best >= 0 ? best : 0;
        }

        // ── Step 2: one GLOBAL left→column map across every row ───────────────
        // Global rather than per-row, so a control at left=105 lands in the same
        // column as one at left=100 several rows up. That is what makes
        // label/value pairs line up down the dialog.
        //
        // Full-width controls are held out of the column map: a lone label with
        // no `width` on its own row is a section heading spanning the dialog, and
        // letting its text drive a column width shoves every real column right.
        //
        // "Alone on its row" counts EVERY control in the band, not just the
        // absolute ones. The original counted absolutes only, which made
        // spellChoose's 'spells' panel look solitary when its 'spellInfo'
        // sibling was merely anchored to it — and two full-width panels cannot
        // sit side by side.
        var fullWidth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, groupElems) in bands)
        {
            if (groupElems.Count != 1) continue;
            var only = groupElems[0];
            if (IsAbsolute(only) && only.Type == DialogControlType.Label && !HasCoord(only.Width))
                fullWidth.Add(only.Id);
        }

        var colBands = new List<int>();
        foreach (var key in bands.SelectMany(b => b.Elems)
                                 .Where(e => IsAbsolute(e) && !fullWidth.Contains(e.Id))
                                 .Select(LeftKey)
                                 .OrderBy(k => k))
        {
            if (!colBands.Any(b => Math.Abs(b - key) <= LeftSnap)) colBands.Add(key);
        }
        colBands.Sort();

        int ColForLeft(int key)
        {
            for (int i = 0; i < colBands.Count; i++)
                if (Math.Abs(colBands[i] - key) <= LeftSnap) return i;
            return 0;
        }

        for (int band = 0; band < bands.Count; band++)
        {
            var groupElems = bands[band].Elems;
            int row = band;

            foreach (var e in groupElems.Where(IsAbsolute))
            {
                if (placed.ContainsKey(e.Id)) continue;
                // A streamBox alone on its row also spans: it is a text panel,
                // not a value sitting in a column.
                bool fw = fullWidth.Contains(e.Id) ||
                          (e.Type == DialogControlType.StreamBox && groupElems.Count == 1);

                placed[e.Id] = new Cell(e)
                {
                    Column        = fw ? 0 : ColForLeft(LeftKey(e)),
                    Row           = row,
                    FullWidth     = fw,
                    CentreAligned = IsCentred(e),
                    RightAligned  = IsRightAligned(e),
                    OriginalTop   = RawTop(e),
                };
            }
        }

        // ── Step 3: bottom-anchored controls (align s/sw/se) ──────────────────
        // The button strip. These sit outside the body grid entirely.
        foreach (var e in elems.Where(IsBottomAnchored))
        {
            if (placed.ContainsKey(e.Id)) continue;
            placed[e.Id] = new Cell(e)
            {
                BottomAnchored = true,
                CentreAligned  = IsCentred(e),
                RightAligned   = IsRightAligned(e),
                OriginalTop    = RawTop(e),
            };
        }

        // ── Step 4: anchored controls, resolved by repeated passes ────────────
        // anchor_left / anchor_right / anchor_top carry a SIBLING CONTROL ID, so
        // a chain resolves only once its target is placed (spellChoose: streamBox
        // 'spellInfo' is anchor_left='spells'). Requeue until nothing more can
        // resolve; a cycle or a dangling id falls out at the cap and is picked up
        // by Step 5 rather than dropped.
        var anchored = elems.Where(e => !IsBottomAnchored(e) && !IsAbsolute(e) &&
                                        !placed.ContainsKey(e.Id)).ToList();
        var queue = new Queue<DialogControl>(anchored);
        int passes = 0, cap = (anchored.Count + 1) * (anchored.Count + 1) + 10;

        while (queue.Count > 0 && passes++ < cap)
        {
            var e = queue.Dequeue();
            if (placed.ContainsKey(e.Id)) continue;

            int? row = null, col = null;
            bool missing = false;
            string? aTop = Anchor(e, "anchor_top");

            if (aTop is not null)
            {
                if (placed.TryGetValue(aTop, out var a)) row = a.Row + 1;
                else missing = true;
            }
            else if (TryCoord(e.Top, out int ownTop) && ownTop != 0)
            {
                row = RowForTop(ownTop);
            }

            if (Anchor(e, "anchor_left") is { } aLeft)
            {
                if (placed.TryGetValue(aLeft, out var a)) { col = a.Column + 1; row ??= a.Row; }
                else missing = true;
            }
            else if (Anchor(e, "anchor_right") is { } aRight)
            {
                if (placed.TryGetValue(aRight, out var a))
                {
                    col = Math.Max(0, a.Column - 1);
                    row ??= a.Row;
                }
                else missing = true;
            }

            if (missing || row is null || (col is null && aTop is null))
            {
                queue.Enqueue(e);
                continue;
            }

            // A streamBox anchored to a SIBLING sits beside it, so it cannot
            // span — only one stacked below (anchor_top) still can. The original
            // spanned every anchored streamBox unconditionally, which collapsed
            // spellChoose's spells/spellInfo pair on top of each other.
            placed[e.Id] = new Cell(e)
            {
                Column        = col ?? 0,
                Row           = row.Value,
                FullWidth     = e.Type == DialogControlType.StreamBox &&
                                Anchor(e, "anchor_left")  is null &&
                                Anchor(e, "anchor_right") is null,
                CentreAligned = IsCentred(e),
                RightAligned  = IsRightAligned(e),
                OriginalTop   = RawTop(e),
            };
        }

        // ── Step 5: anything still unplaced ───────────────────────────────────
        // Controls with no geometry at all, plus anchor cycles and dangling
        // targets. The original dropped these; we flow them onto rows of their
        // own after the grid, so a malformed dialog still renders everything it
        // was actually sent.
        int overflow = placed.Values.Where(c => !c.BottomAnchored)
                             .Select(c => c.Row).DefaultIfEmpty(-1).Max() + 1;
        foreach (var e in elems)
        {
            if (placed.ContainsKey(e.Id)) continue;
            placed[e.Id] = new Cell(e)
            {
                Row           = overflow++,
                FullWidth     = true,
                CentreAligned = IsCentred(e),
                RightAligned  = IsRightAligned(e),
                OriginalTop   = RawTop(e),
            };
        }

        // ── Renumber rows sequentially (anchor_top can leave gaps) ────────────
        var used  = placed.Values.Where(c => !c.BottomAnchored)
                          .Select(c => c.Row).Distinct().OrderBy(r => r).ToList();
        var remap = used.Select((r, i) => (r, i)).ToDictionary(t => t.r, t => t.i);
        foreach (var c in placed.Values.Where(c => !c.BottomAnchored))
            if (remap.TryGetValue(c.Row, out int nr)) c.Row = nr;

        var cells = placed.Values
            .OrderBy(c => order.TryGetValue(c.Control.Id, out int i) ? i : int.MaxValue)
            .Select(c => new DialogGridCell(
                c.Control, c.Column, c.Row, c.FullWidth, c.CentreAligned,
                c.RightAligned, c.BottomAnchored, c.OriginalTop))
            .ToList();

        return new DialogGrid(cells);
    }

    // ── Attribute helpers ─────────────────────────────────────────────────────

    private static bool IsBottomAnchored(DialogControl c) => c.Align is "s" or "se" or "sw";

    private static bool IsCentred(DialogControl c) => c.Align == "center";

    private static bool IsRightAligned(DialogControl c) => c.Align == "ne";

    /// <summary>No anchor attributes and not bottom-anchored — placed by coordinates.</summary>
    private static bool IsAbsolute(DialogControl c) =>
        !IsBottomAnchored(c) &&
        Anchor(c, "anchor_left")  is null &&
        Anchor(c, "anchor_right") is null &&
        Anchor(c, "anchor_top")   is null;

    /// <summary>The sibling control id an anchor attribute points at, if present.</summary>
    private static string? Anchor(DialogControl c, string name) =>
        c.Attributes.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>
    /// Negative tops are measured from the bottom of the dialog; fold them past
    /// every plausible positive value so they sort last and never share a band
    /// with a top-measured control.
    /// </summary>
    private static int NormaliseTop(int top) => top < 0 ? 100_000 + Math.Abs(top) : top;

    private static int RawTop(DialogControl c) => TryCoord(c.Top, out int t) ? t : 0;

    /// <summary>
    /// Column sort key. Negative lefts are measured from the right edge, so they
    /// sort after every left-measured control.
    /// </summary>
    private static int LeftKey(DialogControl c)
    {
        if (!TryCoord(c.Left, out int l)) return 0;
        return l < 0 ? int.MaxValue + l : l;
    }

    private static bool HasCoord(string? raw) => TryCoord(raw, out _);

    /// <summary>
    /// Parses a geometry attribute. DR mixes pixels and percents
    /// (<c>left='20%'</c>, <c>top='160'</c>); the numeric part drives band
    /// inference either way, which beats the original's silent collapse to zero.
    /// </summary>
    internal static bool TryCoord(string? raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.EndsWith('%')) s = s[..^1].TrimEnd();
        return int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private sealed class Cell(DialogControl control)
    {
        public DialogControl Control { get; } = control;
        public int  Column         { get; set; }
        public int  Row            { get; set; }
        public bool FullWidth      { get; set; }
        public bool CentreAligned  { get; set; }
        public bool RightAligned   { get; set; }
        public bool BottomAnchored { get; set; }
        public int  OriginalTop    { get; set; }
    }
}
