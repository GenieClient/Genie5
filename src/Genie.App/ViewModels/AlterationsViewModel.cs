using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Genie.Core.Alterations;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the top-level <b>Alterations</b> menu and the Alteration Designer
/// dialog — Genie 5's first-class replacement for the Genie 4 Alteration Buddy
/// plugin (Djordje, GPL-3.0, github.com/mj-colonel-panic/AlterationBuddy).
///
/// <para>
/// This is deliberately NOT a dockable panel. Designing an alteration is a
/// bounded, occasional task with no live game feed behind it — nothing here
/// updates while you play — so it earns a menu and a dialog, the same shape as
/// Maps ▸ Cross-Zone Connections, rather than a permanent slot in the window
/// layout competing with panels that DO track the session.
/// </para>
///
/// <para>
/// The library is account-level, not per-character: designs are ideas for items,
/// and a player moves them between characters freely. It therefore lives in the
/// shared Config directory (<c>alterations.json</c>) rather than a profile dir —
/// which also matches where Alteration Buddy kept <c>alterations.csv</c>.
/// </para>
/// </summary>
public class AlterationsViewModel : ReactiveObject
{
    private readonly AlterationLibrary _library = new();

    /// <summary>Absolute path to <c>alterations.json</c>. Set once by
    /// MainWindowViewModel when the Config directory is known.</summary>
    public string LibraryPath { get; private set; } = "";

    /// <summary>The saved designs, in file order. Rebuilt from the library after
    /// every mutation so the menu and the dialog list stay in step.</summary>
    public ObservableCollection<AlterationDesign> Designs { get; } = new();

    /// <summary>Last non-fatal message from a load / save / import, surfaced by
    /// the caller in the game window. Empty when the last operation was clean.</summary>
    [Reactive] public string StatusText { get; private set; } = "";

    /// <summary>Points the library at the Config directory and loads it. Safe to
    /// call before any connection — designs are not session state.</summary>
    public void Initialize(string configDir)
    {
        LibraryPath = Path.Combine(configDir, "alterations.json");
        Reload();
    }

    /// <summary>
    /// Re-read the library from disk. A corrupt file is reported and the
    /// in-memory list is left alone: <see cref="AlterationLibrary.Load"/> throws
    /// rather than returning empty precisely so we do not present a blank
    /// designer and then overwrite real designs on the next save.
    /// </summary>
    public void Reload()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return;

        try
        {
            _library.Load(LibraryPath);
            Sync();
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"could not read {Path.GetFileName(LibraryPath)} ({ex.Message}) — " +
                          "your saved designs were left untouched; fix or move the file and reload.";
        }
    }

    /// <summary>Write the library to disk. Returns false and sets
    /// <see cref="StatusText"/> when the write fails.</summary>
    public bool Save()
    {
        if (string.IsNullOrEmpty(LibraryPath)) return false;

        try
        {
            _library.Save(LibraryPath);
            StatusText = "";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"could not write {Path.GetFileName(LibraryPath)} ({ex.Message}).";
            return false;
        }
    }

    /// <summary>Append a new design and persist.</summary>
    public void Add(AlterationDesign design)
    {
        _library.Add(design);
        Sync();
        Save();
    }

    /// <summary>Overwrite the design at <paramref name="index"/> and persist.</summary>
    public void Update(int index, AlterationDesign design)
    {
        _library.Replace(index, design);
        Sync();
        Save();
    }

    /// <summary>Remove the design at <paramref name="index"/> and persist.</summary>
    public void RemoveAt(int index)
    {
        _library.RemoveAt(index);
        Sync();
        Save();
    }

    /// <summary>
    /// Merge an Alteration Buddy <c>alterations.csv</c> into the library.
    /// Imports append rather than replace — a player pulling designs off an old
    /// Genie 4 install should never lose what they have already built here.
    /// Returns the number of designs added.
    /// </summary>
    public int ImportGenie4(string csvPath)
    {
        try
        {
            var added = _library.ImportGenie4Into(csvPath);
            Sync();
            Save();
            StatusText = "";
            return added;
        }
        catch (Exception ex)
        {
            StatusText = $"could not import {Path.GetFileName(csvPath)} ({ex.Message}).";
            return 0;
        }
    }

    /// <summary>Write the library back out in Alteration Buddy's tab-separated
    /// format. Title and Notes are dropped — the old format cannot carry them.</summary>
    public bool ExportGenie4(string csvPath)
    {
        try
        {
            _library.ExportGenie4File(csvPath);
            StatusText = "";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"could not export to {Path.GetFileName(csvPath)} ({ex.Message}).";
            return false;
        }
    }

    private void Sync()
    {
        Designs.Clear();
        foreach (var d in _library.Designs) Designs.Add(d);
    }
}

/// <summary>
/// One entry in the Alterations ▸ Saved Designs submenu. Mirrors
/// <see cref="LayoutMenuItem"/> / <see cref="ThemeMenuItem"/>: a display string
/// plus the index the command needs, so the menu can be rebuilt from the live
/// library on every open without holding view state.
/// </summary>
/// <param name="Index">Position in <see cref="AlterationsViewModel.Designs"/>.</param>
/// <param name="Display">Menu label — the design's title, tap, or short tap.</param>
public readonly record struct AlterationMenuItem(int Index, string Display);
