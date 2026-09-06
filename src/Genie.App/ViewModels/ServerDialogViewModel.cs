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

public sealed class DialogStreamViewModel(DialogGridCell cell) : DialogControlViewModel(cell)
{
    [Reactive] public string Text { get; set; } = "";
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
    }

    public string DialogId { get; }

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
            vm.Text = state.Streams.TryGetValue(vm.Id, out var text) ? text : "";
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
