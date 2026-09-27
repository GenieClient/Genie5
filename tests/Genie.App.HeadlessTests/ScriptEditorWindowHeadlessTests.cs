using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Genie.App.ScriptEditing;
using Genie.App.Views;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #243 — the built-in script editor window, driven headless: dirty
/// marker in the title, Ctrl+S / Ctrl+F, the save-on-close prompt, the
/// changed-on-disk banner, and one window per file.
/// </summary>
public class ScriptEditorWindowHeadlessTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "genie_editorwin_" + Guid.NewGuid().ToString("N"));

    public ScriptEditorWindowHeadlessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string Script(string name, string text)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Type(ScriptEditorWindow w, string text)
    {
        var doc = w.TextEditor.Document;
        doc.Insert(doc.TextLength, text);
    }

    [AvaloniaFact]
    public void Opens_the_file_with_line_numbers_and_highlighting()
    {
        var svc = new ScriptEditorService();
        var w = svc.Open(Script("hunt.cmd", "loop:\nput look\ngoto loop\n"));
        Pump();

        Assert.Equal("loop:\nput look\ngoto loop\n", w.TextEditor.Text);
        Assert.True(w.TextEditor.ShowLineNumbers);
        Assert.Equal("Genie Script", w.TextEditor.SyntaxHighlighting?.Name);
        Assert.False(w.IsDirty);
        Assert.Equal("hunt.cmd — Script Editor", w.Title);
        w.ForceClose();
    }

    [AvaloniaFact]
    public void A_js_file_gets_the_javascript_definition()
    {
        var svc = new ScriptEditorService();
        var w = svc.Open(Script("loot.js", "genie.put('look');\n"));
        Assert.Equal("JavaScript", w.TextEditor.SyntaxHighlighting?.Name);
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Editing_marks_the_title_dirty_and_saving_or_undoing_clears_it()
    {
        var path = Script("a.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);

        Type(w, "echo 2\n");
        Assert.True(w.IsDirty);
        Assert.Equal("a.cmd* — Script Editor", w.Title);

        w.TextEditor.Undo();
        Assert.False(w.IsDirty);                    // back to the saved text
        Assert.Equal("a.cmd — Script Editor", w.Title);

        Type(w, "echo 3\n");
        Assert.True(w.Save());
        Assert.False(w.IsDirty);
        Assert.Equal("a.cmd — Script Editor", w.Title);
        Assert.Equal("echo 1\necho 3\n", File.ReadAllText(path));
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Ctrl_S_saves_and_Ctrl_F_opens_the_search_panel()
    {
        var path = Script("b.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);
        Pump();
        Type(w, "pause\n");

        w.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        Pump();
        Assert.False(w.IsDirty);
        Assert.Equal("echo 1\npause\n", File.ReadAllText(path));

        Assert.True(w.Search!.IsClosed);
        w.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Pump();
        Assert.False(w.Search.IsClosed);
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Closing_a_clean_window_does_not_prompt()
    {
        var w = new ScriptEditorService().Open(Script("c.cmd", "echo 1\n"));
        var asked = false;
        w.ConfirmClose = _ => { asked = true; return Task.FromResult<bool?>(null); };
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.Close();
        Pump();

        Assert.False(asked);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void Closing_with_unsaved_edits_asks_and_cancel_keeps_the_window()
    {
        var w = new ScriptEditorService().Open(Script("d.cmd", "echo 1\n"));
        Type(w, "x");
        var asked = 0;
        w.ConfirmClose = _ => { asked++; return Task.FromResult<bool?>(null); };
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.Close();
        Pump();

        Assert.Equal(1, asked);
        Assert.False(closed);
        Assert.True(w.IsDirty);
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Closing_with_unsaved_edits_can_save_first()
    {
        var path = Script("e.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);
        Type(w, "exit\n");
        w.ConfirmClose = _ => Task.FromResult<bool?>(true);
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.Close();
        Pump();

        Assert.True(closed);
        Assert.Equal("echo 1\nexit\n", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void Closing_with_unsaved_edits_can_discard_them()
    {
        var path = Script("f.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);
        Type(w, "exit\n");
        w.ConfirmClose = _ => Task.FromResult<bool?>(false);
        var closed = false;
        w.Closed += (_, _) => closed = true;

        w.Close();
        Pump();

        Assert.True(closed);
        Assert.Equal("echo 1\n", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void An_outside_change_raises_the_banner_and_reload_takes_it()
    {
        var path = Script("g.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);
        Assert.False(w.IsExternalChangeShown);

        File.WriteAllText(path, "echo from elsewhere\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        w.CheckDiskNow();

        Assert.True(w.IsExternalChangeShown);
        w.ReloadFromDisk();
        Assert.False(w.IsExternalChangeShown);
        Assert.Equal("echo from elsewhere\n", w.TextEditor.Text);
        Assert.False(w.IsDirty);
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Keep_mine_dismisses_the_banner_and_the_next_save_wins()
    {
        var path = Script("h.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);
        Type(w, "echo mine\n");
        File.WriteAllText(path, "echo theirs\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        w.CheckDiskNow();
        Assert.True(w.IsExternalChangeShown);

        w.KeepMine();
        Assert.False(w.IsExternalChangeShown);
        w.CheckDiskNow();
        Assert.False(w.IsExternalChangeShown);      // same change: not raised twice

        Assert.True(w.Save());
        Assert.Equal("echo 1\necho mine\n", File.ReadAllText(path));
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Our_own_save_does_not_raise_the_banner()
    {
        var path = Script("i.cmd", "echo 1\n");
        var w = new ScriptEditorService().Open(path);
        Type(w, "echo 2\n");
        w.Save();
        w.CheckDiskNow();
        Assert.False(w.IsExternalChangeShown);
        w.ForceClose();
    }

    [AvaloniaFact]
    public void Opening_an_open_file_again_focuses_the_same_window()
    {
        var svc  = new ScriptEditorService();
        var path = Script("j.cmd", "echo 1\n");
        var first = svc.Open(path);
        Type(first, "unsaved");

        var second = svc.Open(Path.Combine(_dir, ".", "j.cmd"));   // same file, other spelling

        Assert.Same(first, second);
        Assert.Single(svc.OpenWindows);
        Assert.Equal("echo 1\nunsaved", second.TextEditor.Text);   // edits not reloaded away

        first.ForceClose();
        Pump();
        Assert.Empty(svc.OpenWindows);
        Assert.NotSame(first, svc.Open(path));                      // a fresh window after close
        svc.CloseAll();
    }

    [AvaloniaFact]
    public void The_service_reports_and_saves_unsaved_windows()
    {
        var svc = new ScriptEditorService();
        var a = svc.Open(Script("k.cmd", "1\n"));
        var b = svc.Open(Script("l.cmd", "2\n"));
        Assert.False(svc.HasUnsavedChanges);

        Type(b, "more\n");
        Assert.True(svc.HasUnsavedChanges);
        Assert.Equal(new[] { b }, svc.DirtyWindows.ToArray());

        Assert.True(svc.SaveAll());
        Assert.False(svc.HasUnsavedChanges);
        Assert.Equal("2\nmore\n", File.ReadAllText(Path.Combine(_dir, "l.cmd")));

        Type(a, "x");
        svc.CloseAll();                             // forced: no prompt
        Pump();
        Assert.Empty(svc.OpenWindows);
    }
}
