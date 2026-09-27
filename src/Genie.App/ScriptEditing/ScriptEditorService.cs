using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Genie.App.Views;

namespace Genie.App.ScriptEditing;

/// <summary>
/// Owns the open built-in script editor windows (public #243): one per file.
/// Opening a file that already has a window brings that window forward instead
/// of making a second copy — two windows over one file would each think the
/// other's save was an outside change.
/// </summary>
public sealed class ScriptEditorService
{
    // Case-insensitive where the filesystem usually is (Windows, macOS), so
    // "Hunt.cmd" and "hunt.cmd" find the same window.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<string, ScriptEditorWindow> _open = new(PathComparer);

    /// <summary>Raised with a one-line note for the Game window (encoding
    /// fallbacks on save, open failures).</summary>
    public event Action<string>? Notice;

    /// <summary>Every open editor window.</summary>
    public IReadOnlyCollection<ScriptEditorWindow> OpenWindows => _open.Values;

    /// <summary>True when any open editor has unsaved edits.</summary>
    public bool HasUnsavedChanges => _open.Values.Any(w => w.IsDirty);

    /// <summary>The open editors with unsaved edits.</summary>
    public IReadOnlyList<ScriptEditorWindow> DirtyWindows => _open.Values.Where(w => w.IsDirty).ToList();

    /// <summary>The window already editing <paramref name="path"/>, if any.</summary>
    public ScriptEditorWindow? Find(string path)
        => _open.TryGetValue(Path.GetFullPath(path), out var w) ? w : null;

    /// <summary>
    /// Open <paramref name="path"/> in an editor window, or bring its existing
    /// window forward. Throws on a read failure (the caller reports it).
    /// </summary>
    public ScriptEditorWindow Open(string path)
    {
        var full = Path.GetFullPath(path);
        if (_open.TryGetValue(full, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.TextEditor.Focus();
            return existing;
        }

        var file = ScriptTextFile.Open(full, out var text);
        var win  = new ScriptEditorWindow(file, text);
        win.Saved += (_, note) => { if (note is not null) Notice?.Invoke($"[editor] {note}"); };
        win.Closed += (_, _) =>
        {
            if (_open.TryGetValue(full, out var w) && ReferenceEquals(w, win)) _open.Remove(full);
        };
        _open[full] = win;
        win.Show();
        win.TextEditor.Focus();
        return win;
    }

    /// <summary>Save every editor with unsaved edits. False when any save failed
    /// (that window keeps its edits and says why).</summary>
    public bool SaveAll()
    {
        var ok = true;
        foreach (var w in DirtyWindows)
            ok &= w.Save();
        return ok;
    }

    /// <summary>Close every editor without prompting (the caller has already
    /// dealt with unsaved edits).</summary>
    public void CloseAll()
    {
        foreach (var w in _open.Values.ToList())
            w.ForceClose();
    }
}
