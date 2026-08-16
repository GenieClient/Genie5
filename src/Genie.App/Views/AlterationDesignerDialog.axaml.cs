using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Genie.App.ViewModels;
using Genie.Core.Alterations;

namespace Genie.App.Views;

/// <summary>
/// The Alteration Designer — Alterations ▸ Open Designer.
///
/// <para>
/// A port of Alteration Buddy's MainForm (Djordje, GPL-3.0,
/// github.com/mj-colonel-panic/AlterationBuddy) onto Avalonia: the four design
/// fields with live length counters on the left, the saved library on the
/// right, and the composed request line underneath. All measurement and
/// formatting lives in <see cref="AlterationValidator"/> /
/// <see cref="AlterationFormatter"/> in Core, so this file is purely wiring.
/// </para>
///
/// <para>
/// Deliberately code-behind rather than bound to a view model, matching
/// <see cref="MapperSettingsDialog"/>: this project does not enable
/// <c>AvaloniaUseCompiledBindingsByDefault</c>, so a mistyped binding path
/// compiles clean and ships a dead control. Named controls wired in C# cannot
/// fail that way.
/// </para>
/// </summary>
public partial class AlterationDesignerDialog : Window
{
    private readonly AlterationsViewModel? _vm;

    /// <summary>Index into the LIBRARY that the editor is currently editing, or
    /// -1 for an unsaved new design. Save updates in place when set, appends
    /// when not — the role Alteration Buddy's <c>LoadedFromIndex</c> played,
    /// minus its "0 means new" overloading of a real list position.</summary>
    private int _editingIndex = -1;

    /// <summary>The rows currently displayed, in display order, each carrying its
    /// library index. The list is filtered and re-sorted (drafts above completed
    /// work), so a row's position is NOT its library index — every action reads
    /// the index from here instead.</summary>
    private IReadOnlyList<AlterationEntry> _rows = Array.Empty<AlterationEntry>();

    private bool _suppressUpdates;

    public AlterationDesignerDialog() { InitializeComponent(); }

    public AlterationDesignerDialog(AlterationsViewModel vm, int loadIndex = -1) : this()
    {
        _vm = vm;

        ShortTapBox.TextChanged += (_, _) => Recalculate();
        TapBox.TextChanged      += (_, _) => Recalculate();
        LookBox.TextChanged     += (_, _) => Recalculate();
        ReadBox.TextChanged     += (_, _) => Recalculate();

        FilterBox.SelectionChanged += (_, _) => RefreshList();

        RefreshList();

        if (loadIndex >= 0 && loadIndex < _vm.Designs.Count)
            LoadIntoEditor(loadIndex);
        else
            Recalculate();
    }

    // ── Library list ────────────────────────────────────────────────────────

    private AlterationFilter CurrentFilter => FilterBox.SelectedIndex switch
    {
        1 => AlterationFilter.Drafts,
        2 => AlterationFilter.Completed,
        _ => AlterationFilter.All
    };

    /// <summary>Rebuild the list from the library under the current filter,
    /// preserving the selected DESIGN (by library index) rather than the selected
    /// row — the row a design sits on moves when it is marked done.</summary>
    private void RefreshList()
    {
        if (_vm is null) return;

        var previouslySelected = SelectedLibraryIndex();

        _rows = _vm.InDisplayOrder(CurrentFilter);
        DesignList.ItemsSource = _rows.Select(RowLabel).ToList();

        var restored = _rows.Select((e, row) => (e, row))
                            .FirstOrDefault(t => t.e.Index == previouslySelected);
        DesignList.SelectedIndex = previouslySelected >= 0 && restored.e.Index == previouslySelected
            ? restored.row
            : -1;

        LibraryHint.Text = DescribeLibrary();
        UpdateToggleButton();
    }

    /// <summary>A completed design is marked with a check so the two groups stay
    /// distinguishable even when the filter is showing everything.</summary>
    private static string RowLabel(AlterationEntry entry) =>
        (entry.Design.IsCompleted ? "✓  " : "") + entry.Design.DisplayName;

    private string DescribeLibrary()
    {
        if (_vm is null) return "";

        if (_vm.Designs.Count == 0)
            return "No saved designs yet. Fill in the fields and press Save.";

        if (_rows.Count == 0)
            return CurrentFilter == AlterationFilter.Completed
                ? "Nothing completed yet."
                : "No drafts — everything here is marked done.";

        return $"{_vm.DraftCount} draft(s), {_vm.CompletedCount} completed. " +
                "Double-click to load a design into the editor.";
    }

    /// <summary>Library index of the selected row, or -1.</summary>
    private int SelectedLibraryIndex()
    {
        var row = DesignList.SelectedIndex;
        return row >= 0 && row < _rows.Count ? _rows[row].Index : -1;
    }

    private void UpdateToggleButton()
    {
        var index = SelectedLibraryIndex();
        var done  = index >= 0 && _vm is not null && _vm.Designs[index].IsCompleted;

        ToggleDoneButton.Content   = done ? "Mark _Draft" : "Mark _Done";
        ToggleDoneButton.IsEnabled = index >= 0;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateToggleButton();

    private void LoadIntoEditor(int libraryIndex)
    {
        if (_vm is null || libraryIndex < 0 || libraryIndex >= _vm.Designs.Count) return;

        var design = _vm.Designs[libraryIndex];

        _suppressUpdates        = true;
        TitleBox.Text           = design.Title;
        ShortTapBox.Text        = design.ShortTap;
        TapBox.Text             = design.Tap;
        LookBox.Text            = design.Look;
        ReadBox.Text            = design.Read;
        NotesBox.Text           = design.Notes;
        CompletedCheck.IsChecked = design.IsCompleted;
        _suppressUpdates        = false;

        _editingIndex = libraryIndex;
        SelectLibraryIndex(libraryIndex);
        Recalculate();
        SetStatus($"Editing “{design.DisplayName}”.");
    }

    /// <summary>Select the row showing a given library index, if it is visible
    /// under the current filter.</summary>
    private void SelectLibraryIndex(int libraryIndex)
    {
        for (var row = 0; row < _rows.Count; row++)
            if (_rows[row].Index == libraryIndex)
            {
                DesignList.SelectedIndex = row;
                UpdateToggleButton();
                return;
            }

        DesignList.SelectedIndex = -1;
        UpdateToggleButton();
    }

    private AlterationDesign CurrentDesign() => new()
    {
        Title       = TitleBox.Text    ?? "",
        ShortTap    = ShortTapBox.Text ?? "",
        Tap         = TapBox.Text      ?? "",
        Look        = LookBox.Text     ?? "",
        Read        = ReadBox.Text     ?? "",
        Notes       = NotesBox.Text    ?? "",
        IsCompleted = CompletedCheck.IsChecked == true
    };

    // ── Live counters + result ──────────────────────────────────────────────

    /// <summary>Recompute every counter and the composed result. Cheap (a few
    /// string measurements) and only ever driven by keystrokes in this dialog.</summary>
    private void Recalculate()
    {
        if (_suppressUpdates) return;

        var design = CurrentDesign();

        ShortTapCount.Text = AlterationValidator.DescribeShortTap(design.ShortTap);
        TapCount.Text      = AlterationValidator.TapBudget(design.Tap).Describe();
        LookCount.Text     = AlterationValidator.LookBudget(design.Look).Describe();

        // Read is budgeted twice — words and characters — reported on one line.
        ReadCount.Text = AlterationValidator.DescribeRead(design.Read);

        ResultBox.Text = AlterationFormatter.Format(design);
    }

    // ── Buttons ─────────────────────────────────────────────────────────────

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        _suppressUpdates = true;
        TitleBox.Text = ShortTapBox.Text = TapBox.Text = LookBox.Text = ReadBox.Text = NotesBox.Text = "";
        CompletedCheck.IsChecked = false;
        _suppressUpdates = false;

        _editingIndex            = -1;
        DesignList.SelectedIndex = -1;
        UpdateToggleButton();
        Recalculate();
        SetStatus("New design.");
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var design = CurrentDesign();
        if (design.IsEmpty)
        {
            SetStatus("Nothing to save — fill in at least one field.");
            return;
        }

        if (_editingIndex >= 0 && _editingIndex < _vm.Designs.Count)
            _vm.Update(_editingIndex, design);
        else
        {
            _vm.Add(design);
            _editingIndex = _vm.Designs.Count - 1;
        }

        RefreshList();
        SelectLibraryIndex(_editingIndex);

        // Over-budget designs still save: a player may be drafting, or the GM
        // may allow more. Say so rather than blocking the save.
        var problems = AlterationValidator.Problems(design);
        SetStatus(!string.IsNullOrEmpty(_vm.StatusText)
            ? _vm.StatusText
            : problems.Count == 0
                ? $"Saved “{design.DisplayName}”."
                : $"Saved “{design.DisplayName}” — over budget: {string.Join(" ", problems)}");
    }

    private void OnLoad(object? sender, RoutedEventArgs e) => LoadIntoEditor(SelectedLibraryIndex());

    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => LoadIntoEditor(SelectedLibraryIndex());

    /// <summary>
    /// Flip the selected design between draft and completed without loading it
    /// into the editor — the point being to file finished work away quickly.
    /// The row moves (and may vanish under a filter), so re-anchor the selection
    /// on the design rather than the row.
    /// </summary>
    private void OnToggleCompleted(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var index = SelectedLibraryIndex();
        if (index < 0)
        {
            SetStatus("Select a design first.");
            return;
        }

        var design = _vm.Designs[index];
        var name   = design.DisplayName;
        var done   = !design.IsCompleted;

        if (!_vm.SetCompleted(index, done)) return;

        // Keep the editor's checkbox honest if it is showing this same design.
        if (_editingIndex == index)
        {
            _suppressUpdates         = true;
            CompletedCheck.IsChecked = done;
            _suppressUpdates         = false;
        }

        RefreshList();
        SelectLibraryIndex(index);
        SetStatus(done ? $"Marked “{name}” completed." : $"Moved “{name}” back to drafts.");
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var index = SelectedLibraryIndex();
        if (index < 0)
        {
            SetStatus("Select a design to delete.");
            return;
        }

        var name = _vm.Designs[index].DisplayName;
        var ok   = await new ConfirmDialog("Confirm Delete",
                        $"Delete your design for “{name}”?").ShowDialog<bool>(this);
        if (!ok) return;

        _vm.RemoveAt(index);

        // The editor may have been pointed at the removed design, or at one that
        // just shifted down; re-anchor rather than silently overwriting a
        // neighbour on the next Save.
        if (_editingIndex == index)      _editingIndex = -1;
        else if (_editingIndex > index)  _editingIndex--;

        RefreshList();
        SetStatus($"Deleted “{name}”.");
    }

    private async void OnCopyResult(object? sender, RoutedEventArgs e)
    {
        var text = ResultBox.Text ?? "";
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Nothing to copy yet.");
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                SetStatus("Result copied to the clipboard.");
            }
        }
        catch (Exception ex)
        {
            Genie.App.Diagnostics.ErrorLog.Log("AlterationDesignerDialog.CopyResult", ex);
            SetStatus("Could not reach the clipboard.");
        }
    }

    private void OnOpenGuide(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("https://elanthipedia.play.net/Alteration")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Genie.App.Diagnostics.ErrorLog.Log("AlterationDesignerDialog.OpenGuide", ex);
            SetStatus("Could not open a browser — visit elanthipedia.play.net/Alteration manually.");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) => StatusText.Text = text;
}
