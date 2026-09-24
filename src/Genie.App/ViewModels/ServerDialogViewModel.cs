using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>Base for one rendered control in a server dialog (#156 Phase 1).
/// Placement comes from <see cref="DialogGridLayout"/>; the view binds a Grid
/// cell to <see cref="Row"/>/<see cref="Column"/>.</summary>
public abstract class DialogControlViewModel : ReactiveObject
{
    protected DialogControlViewModel(DialogGridCell cell) => Id = cell.Id;

    public string Id { get; }

    [Reactive] public int  Row           { get; set; }
    [Reactive] public int  Column        { get; set; }
    [Reactive] public bool FullWidth     { get; set; }
    /// <summary>Grid columns to span — the whole row for a full-width control.</summary>
    [Reactive] public int  ColumnSpan    { get; set; } = 1;
    [Reactive] public bool CentreAligned { get; set; }
    [Reactive] public bool RightAligned  { get; set; }
    [Reactive] public bool IsEnabled     { get; set; } = true;

    /// <summary>Apply a fresh delta to a control already on screen. Overrides
    /// update their own bound state; anything the USER owns (typed text, a
    /// selection) is deliberately left alone unless the server changed it.</summary>
    public virtual void Update(DialogGridCell cell)
    {
        Row           = cell.Row;
        Column        = cell.Column;
        FullWidth     = cell.FullWidth;
        CentreAligned = cell.CentreAligned;
        RightAligned  = cell.RightAligned;
        IsEnabled     = !string.Equals(Attr(cell, "enabled"), "false",
                                       StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The control's current value, as the command resolver wants it.
    /// Null means "nothing the user has changed" — fall back to the server's.</summary>
    public virtual string? LiveValue => null;

    protected static string? Attr(DialogGridCell cell, string name) =>
        cell.Control.Attributes.TryGetValue(name, out var v) ? v : null;
}

public sealed class DialogLabelViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Text { get; set; } = "";
    [Reactive] public bool   Wrap { get; set; }

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        Text = c.Control.Value ?? c.Control.Text ?? "";
        Wrap = string.Equals(Attr(c, "wrap"), "true", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DialogButtonViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Caption { get; set; } = "";

    /// <summary>A <c>closeButton</c> sends its command (if any) and then closes
    /// the window.</summary>
    public bool ClosesDialog { get; private set; }

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        Caption      = c.Control.Value ?? c.Control.Text ?? "";
        ClosesDialog = c.Control.Type == DialogControlType.CloseButton;
    }
}

/// <summary>
/// A <c>&lt;link&gt;</c> control (#342). It carries a caption and a cmd, so
/// before this existed it fell through to the label template and rendered as
/// ordinary dead text — the caption was readable and the command unreachable,
/// because only the button templates wire an activation handler.
///
/// <para>The dispatch behind it was already complete: <c>ServerDialogCommand</c>
/// classifies the <c>url:</c> form and the controller routes it to the web-link
/// safety prompt, with game commands going to the normal input path. What was
/// missing was a control that could trigger any of it.</para>
/// </summary>
public sealed class DialogLinkViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string  Caption { get; set; } = "";
    [Reactive] public string? Tooltip { get; set; }

    /// <summary>True when the cmd opens a web page rather than sending a game
    /// command — surfaced so the view can say so before the user clicks.</summary>
    [Reactive] public bool IsExternal { get; set; }

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        Caption    = c.Control.Value ?? c.Control.Text ?? c.Control.Id;
        IsExternal = c.Control.Cmd?.StartsWith("url:", StringComparison.OrdinalIgnoreCase) == true;
        Tooltip    = Attr(c, "tooltip")
                  ?? (IsExternal ? "Opens in your web browser" : c.Control.Cmd);
    }
}

/// <summary>
/// An <c>&lt;image&gt;</c> or <c>&lt;skin&gt;</c> control (#341).
///
/// <para>These carry neither <c>value</c> nor <c>text</c>, so falling through to
/// the label template made every one of them an empty row that occupied a grid
/// cell and showed nothing. <c>befriend</c> ("Friends and Enemies") is seven
/// image controls and nothing else, and it arrives in the login block — so the
/// dialog most users meet first opened as an essentially empty window.</para>
///
/// <para>This is the legible-placeholder half: the sprite name is rendered as
/// text, and the control activates if it carries a cmd (the empath-facing
/// <c>injuries-&lt;charnum&gt;</c> images carry <c>transfer …</c> commands and
/// tooltips that were being dropped with the image). Real sprite rendering,
/// reusing the injuries panel's mapping, is tracked separately as #345.</para>
/// </summary>
public sealed class DialogImageViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    /// <summary>What the server called the sprite — <c>name</c>, falling back to
    /// <c>id</c>. Never empty: a blank row is the bug this replaces.</summary>
    [Reactive] public string  Label   { get; set; } = "";
    [Reactive] public string? Tooltip { get; set; }

    /// <summary>True when the image carries a cmd, so the view renders it as
    /// something clickable rather than as static text.</summary>
    [Reactive] public bool IsActivatable { get; set; }

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        var name = Attr(c, "name");
        Label = !string.IsNullOrWhiteSpace(name) ? name!
              : !string.IsNullOrWhiteSpace(c.Control.Id) ? c.Control.Id
              : "(image)";
        IsActivatable = !string.IsNullOrWhiteSpace(c.Control.Cmd);
        Tooltip       = Attr(c, "tooltip") ?? c.Control.Cmd;
    }
}

public sealed class DialogTextBoxViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Value { get; set; } = "";

    /// <summary>True for <c>upDownEditBox</c> — a numeric spinner.</summary>
    public bool  IsNumeric { get; private set; }
    public double Minimum  { get; private set; }
    public double Maximum  { get; private set; } = double.MaxValue;
    public int    MaxChars { get; private set; }

    private bool _seeded;

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        IsNumeric = c.Control.Type == DialogControlType.UpDownEditBox;
        if (double.TryParse(Attr(c, "min"), out var min)) Minimum = min;
        if (double.TryParse(Attr(c, "max"), out var max)) Maximum = max;
        if (int.TryParse(Attr(c, "maxChars"), out var mc)) MaxChars = mc;

        // Seed once. A later delta must not yank the field out from under
        // someone mid-type — the server re-sends its own default on every
        // block, and overwriting would make the box impossible to edit.
        if (!_seeded)
        {
            Value   = c.Control.Value ?? "";
            _seeded = true;
        }
    }

    public override string? LiveValue => Value;
}

public sealed class DialogCheckBoxViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Caption   { get; set; } = "";
    [Reactive] public bool   IsChecked { get; set; }

    private bool _seeded;

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        Caption = c.Control.Text ?? c.Control.Value ?? "";
        if (!_seeded)
        {
            IsChecked = Truthy(Attr(c, "checked"));
            _seeded   = true;
        }
    }

    public override string? LiveValue => IsChecked ? "1" : "0";

    internal static bool Truthy(string? s) =>
        s is not null &&
        (s.Equals("t", StringComparison.OrdinalIgnoreCase) ||
         s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         s.Equals("y", StringComparison.OrdinalIgnoreCase) ||
         s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         s == "1");
}

public sealed class DialogRadioViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Caption   { get; set; } = "";
    [Reactive] public bool   IsChecked { get; set; }

    /// <summary>Radios sharing a <c>group</c> are mutually exclusive; the group
    /// name is also the <c>%placeholder%</c> token they answer to.</summary>
    public string GroupName { get; private set; } = "";

    private bool _seeded;

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        Caption   = c.Control.Text ?? c.Control.Value ?? "";
        GroupName = Attr(c, "group") ?? "";
        if (!_seeded)
        {
            IsChecked = DialogCheckBoxViewModel.Truthy(Attr(c, "checked"));
            _seeded   = true;
        }
    }

    public override string? LiveValue => IsChecked ? "1" : "0";
}

public sealed class DialogComboViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    public ObservableCollection<string> Items { get; } = [];

    [Reactive] public string? Selected { get; set; }

    /// <summary>A dropDownBox fires its own command as soon as the selection
    /// changes, as well as feeding any button that names it.</summary>
    public bool HasOwnCommand { get; private set; }

    private bool _seeded;

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        HasOwnCommand = !string.IsNullOrWhiteSpace(c.Control.Cmd);

        var texts = (Attr(c, "content_text") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .ToList();

        if (!texts.SequenceEqual(Items))
        {
            var keep = Selected;
            Items.Clear();
            foreach (var t in texts) Items.Add(t);
            // Hold the user's choice across a refresh when it is still offered.
            Selected = keep is not null && Items.Contains(keep) ? keep : null;
            _seeded  = false;
        }

        if (!_seeded)
        {
            Selected ??= c.Control.Value is { Length: > 0 } v && Items.Contains(v)
                ? v
                : Items.FirstOrDefault();
            _seeded = true;
        }
    }

    public override string? LiveValue => Selected;
}

/// <summary>One run of a streamBox line: plain text, or a clickable link.</summary>
public sealed record DialogStreamSegment(string Text, LinkSpan? Link)
{
    public bool IsLink => Link is not null;
}

/// <summary>One line of a streamBox, as segments.</summary>
public sealed record DialogStreamLine(IReadOnlyList<DialogStreamSegment> Segments);

public sealed class DialogStreamViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Text { get; private set; } = "";

    /// <summary>The same text split into lines of plain and link segments, for
    /// the view. Links are what make spellChoose work: its list is one link per
    /// spell, and clicking one is the only way to choose (#156).</summary>
    public ObservableCollection<DialogStreamLine> Lines { get; } = [];

    /// <summary>The size DR asked for, in pixels; NaN = size to content. The
    /// box used to ignore it and cap at 80–380px, so spellChoose's 380px-tall
    /// lists opened as 80px slivers.</summary>
    [Reactive] public double BoxWidth  { get; private set; } = double.NaN;
    [Reactive] public double BoxHeight { get; private set; } = double.NaN;

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        BoxWidth  = Pixels(c.Control.Width);
        BoxHeight = Pixels(c.Control.Height);
    }

    private static double Pixels(string? raw) =>
        int.TryParse(raw, System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : double.NaN;

    public void SetContent(string text, IReadOnlyList<LinkSpan>? links)
    {
        text ??= "";
        if (text == Text && Lines.Count > 0 && links is null) return;
        Text = text;
        Lines.Clear();
        foreach (var line in Split(text, links ?? Array.Empty<LinkSpan>()))
            Lines.Add(line);
    }

    /// <summary>Cut <paramref name="text"/> into lines, and each line into
    /// plain/link segments. A link that crosses a line break is clipped to each
    /// line it touches.</summary>
    public static IEnumerable<DialogStreamLine> Split(string text, IReadOnlyList<LinkSpan> links)
    {
        var ordered = links.Where(l => l.Length > 0).OrderBy(l => l.Start).ToList();
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var nl      = text.IndexOf('\n', lineStart);
            var lineEnd = nl < 0 ? text.Length : nl;
            var segs    = new List<DialogStreamSegment>();
            var pos     = lineStart;
            foreach (var l in ordered)
            {
                var s = Math.Max(l.Start, pos);
                var e = Math.Min(l.Start + l.Length, lineEnd);
                if (e <= s) continue;
                if (s > pos) segs.Add(new(text[pos..s], null));
                segs.Add(new(text[s..e], l));
                pos = e;
            }
            if (lineEnd > pos) segs.Add(new(text[pos..lineEnd].TrimEnd('\r'), null));
            if (segs.Count > 0 || nl >= 0) yield return new DialogStreamLine(segs);
            if (nl < 0) break;
            lineStart = nl + 1;
        }
    }
}

public sealed class DialogProgressViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public double Value   { get; set; }
    [Reactive] public string Caption { get; set; } = "";

    public override void Update(DialogGridCell c)
    {
        base.Update(c);
        Caption = c.Control.Text ?? "";
        if (double.TryParse(c.Control.Value, out var v)) Value = Math.Clamp(v, 0, 100);
    }
}

/// <summary>
/// Backs one server-driven dialog window (#156 Phase 1): turns a
/// <see cref="ServerDialogState"/> into bound controls laid out on the inferred
/// grid, and turns a click into a resolved command.
///
/// <para>Controls are MERGED in place rather than rebuilt. A dialogData block is
/// a delta and the server re-sends its own defaults freely; rebuilding on every
/// one would blow away half-typed text and reset selections mid-interaction.</para>
/// </summary>
public sealed class ServerDialogViewModel : ReactiveObject
{
    private readonly Dictionary<string, DialogControlViewModel> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<DialogControl> _controls = [];

    public ServerDialogViewModel(string dialogId, char separatorChar = ';')
    {
        DialogId      = dialogId;
        SeparatorChar = separatorChar;
        Title         = dialogId;
        Bespoke       = ServerDialogOverrides.Create(this);
    }

    public string DialogId { get; }

    /// <summary>A hand-built view for this dialog (#156 Phase 2), or null for
    /// the generic grid. Fed the same snapshots; clicks come back through
    /// <see cref="Activate"/>.</summary>
    public IServerDialogBespoke? Bespoke { get; }

    /// <summary>True when the generic grid renders this dialog.</summary>
    public bool IsGeneric => Bespoke is null;

    /// <summary>The command separator in force — server-authored commands are
    /// escaped against it so they cannot fan out (see
    /// <see cref="ServerDialogCommand"/>).</summary>
    public char SeparatorChar { get; set; }

    [Reactive] public string Title   { get; set; }
    [Reactive] public int    Columns { get; set; }
    [Reactive] public int    Rows    { get; set; }

    /// <summary>Grid definition strings for the view. Every column and row sizes
    /// to its content — which is the whole point of inferring a grid instead of
    /// honouring the server's pixel coordinates.</summary>
    [Reactive] public string ColumnSpec { get; set; } = "Auto";
    [Reactive] public string RowSpec    { get; set; } = "Auto";

    /// <summary>Body controls, then centred ones, then the bottom button strip.</summary>
    public ObservableCollection<DialogControlViewModel> Controls { get; } = [];

    /// <summary>The bottom-anchored button strip, rendered under the grid.</summary>
    public ObservableCollection<DialogControlViewModel> BottomControls { get; } = [];

    /// <summary>Raised when a control is activated and resolved to an action.
    /// The host sends it or opens the browser — this VM never does either
    /// itself, so it stays testable headless.</summary>
    public event Action<ServerDialogAction>? ActionRequested;

    /// <summary>Raised when a <c>closeButton</c> asks for the window to close.</summary>
    public event Action? CloseRequested;

    // ── State in ─────────────────────────────────────────────────────────────

    public void Apply(ServerDialogState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Title     = string.IsNullOrWhiteSpace(state.Title) ? state.Id : state.Title!;
        _controls = state.Controls;

        var grid = state.Grid;
        Columns = Math.Max(grid.Columns, 1);
        Rows    = Math.Max(grid.Rows, 1);

        ColumnSpec = string.Join(",", Enumerable.Repeat("Auto", Columns));
        RowSpec    = string.Join(",", Enumerable.Repeat("Auto", Rows));

        Sync(Controls,       grid.Body.Concat(grid.CentreBody).ToList());
        Sync(BottomControls, grid.Bottom);

        // A full-width control spans every column; anything else occupies one.
        foreach (var vm in Controls)
            vm.ColumnSpan = vm.FullWidth ? Columns : 1;

        // streamBox content arrives separately, via dynaStream (#324).
        foreach (var vm in Controls.Concat(BottomControls).OfType<DialogStreamViewModel>())
            vm.SetContent(state.Streams.TryGetValue(vm.Id, out var text) ? text : "",
                          state.StreamLinks.TryGetValue(vm.Id, out var links) ? links : null);

        Bespoke?.Apply(state);
    }

    private void Sync(
        ObservableCollection<DialogControlViewModel> target, IReadOnlyList<DialogGridCell> cells)
    {
        // Drop controls the server no longer sends (a `clear` reset, or a
        // dialog that swapped its contents).
        var live = new HashSet<string>(cells.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (live.Contains(target[i].Id)) continue;
            _byId.Remove(target[i].Id);
            target.RemoveAt(i);
        }

        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (!_byId.TryGetValue(cell.Id, out var vm) || !Matches(vm, cell))
            {
                if (vm is not null)
                {
                    // Same id, different control type — replace it outright.
                    _byId.Remove(cell.Id);
                    var at = target.IndexOf(vm);
                    if (at >= 0) target.RemoveAt(at);
                }
                vm = Create(cell);
                _byId[cell.Id] = vm;
                target.Insert(Math.Min(i, target.Count), vm);
            }
            vm.Update(cell);
        }
    }

    private static bool Matches(DialogControlViewModel vm, DialogGridCell cell) =>
        vm.GetType() == VmTypeFor(cell.Control.Type);

    private static Type VmTypeFor(DialogControlType type) => type switch
    {
        DialogControlType.CmdButton or DialogControlType.CloseButton
            => typeof(DialogButtonViewModel),
        DialogControlType.EditBox or DialogControlType.UpDownEditBox
            => typeof(DialogTextBoxViewModel),
        DialogControlType.CheckBox    => typeof(DialogCheckBoxViewModel),
        DialogControlType.Radio       => typeof(DialogRadioViewModel),
        DialogControlType.DropDownBox => typeof(DialogComboViewModel),
        DialogControlType.StreamBox   => typeof(DialogStreamViewModel),
        DialogControlType.ProgressBar => typeof(DialogProgressViewModel),
        DialogControlType.Link        => typeof(DialogLinkViewModel),
        DialogControlType.Image or DialogControlType.Skin
                                      => typeof(DialogImageViewModel),
        _                             => typeof(DialogLabelViewModel),
    };

    private static DialogControlViewModel Create(DialogGridCell cell) => cell.Control.Type switch
    {
        DialogControlType.CmdButton or DialogControlType.CloseButton
            => new DialogButtonViewModel(cell),
        DialogControlType.EditBox or DialogControlType.UpDownEditBox
            => new DialogTextBoxViewModel(cell),
        DialogControlType.CheckBox    => new DialogCheckBoxViewModel(cell),
        DialogControlType.Radio       => new DialogRadioViewModel(cell),
        DialogControlType.DropDownBox => new DialogComboViewModel(cell),
        DialogControlType.StreamBox   => new DialogStreamViewModel(cell),
        DialogControlType.ProgressBar => new DialogProgressViewModel(cell),
        DialogControlType.Link        => new DialogLinkViewModel(cell),
        DialogControlType.Image or DialogControlType.Skin
                                      => new DialogImageViewModel(cell),
        _                             => new DialogLabelViewModel(cell),
    };

    // ── Activation out ───────────────────────────────────────────────────────

    /// <summary>
    /// A control was clicked, toggled or committed. Resolves its command against
    /// what every sibling currently holds and raises
    /// <see cref="ActionRequested"/>; a closeButton also asks to close, whether
    /// or not it carried a command.
    /// </summary>
    public void Activate(string controlId)
    {
        var control = _controls.FirstOrDefault(
            c => string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));

        var action = ServerDialogCommand.Resolve(
            control?.Cmd, _controls, LiveValues(), SeparatorChar);

        if (action.CanSend) ActionRequested?.Invoke(action);

        if (_byId.TryGetValue(controlId, out var vm) &&
            vm is DialogButtonViewModel { ClosesDialog: true })
            CloseRequested?.Invoke();
    }

    /// <summary>
    /// A link inside a streamBox was clicked. Resolved like a control's cmd —
    /// against the siblings, separator-escaped — and a web link goes through
    /// the same <c>url:</c> path, so the host's safety prompt still applies.
    /// </summary>
    public void ActivateStreamLink(LinkSpan link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var cmd = link.IsUrl ? "url:" + link.Command : link.Command;
        var action = ServerDialogCommand.Resolve(cmd, _controls, LiveValues(), SeparatorChar);
        if (action.CanSend) ActionRequested?.Invoke(action);
    }

    /// <summary>
    /// A radio was selected — clear its group siblings first, since the view
    /// binds each one independently rather than through a shared group source.
    /// </summary>
    public void SelectRadio(string controlId)
    {
        if (!_byId.TryGetValue(controlId, out var picked) ||
            picked is not DialogRadioViewModel radio) return;

        foreach (var other in _byId.Values.OfType<DialogRadioViewModel>())
            if (!ReferenceEquals(other, radio) && other.GroupName == radio.GroupName)
                other.IsChecked = false;

        radio.IsChecked = true;
    }

    /// <summary>What every control currently holds, for command resolution.</summary>
    public IReadOnlyDictionary<string, string> LiveValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, vm) in _byId)
            if (vm.LiveValue is { } v) values[id] = v;
        return values;
    }

    /// <summary>Resolve a control's command without firing it — lets the view
    /// disable a button whose placeholders are not satisfied yet.</summary>
    public ServerDialogAction Preview(string controlId)
    {
        var control = _controls.FirstOrDefault(
            c => string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase));
        return ServerDialogCommand.Resolve(control?.Cmd, _controls, LiveValues(), SeparatorChar);
    }
}
