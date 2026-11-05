using System.Collections.ObjectModel;
using Genie.Core.Update;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>Input to the per-file chooser: the source's name and every script
/// file that is new or differs upstream (already-ignored ones flagged).</summary>
public sealed record ScriptFilesRequest(string SourceName, IReadOnlyList<ScriptFileChange> Files);

/// <summary>What the user chose: the complete new ignore list for the source,
/// and the files to pull right now (empty = just save the ignore list).</summary>
public sealed record ScriptFilesResult(IReadOnlyList<string> Ignored, IReadOnlyList<string> UpdateNow);

/// <summary>One file row in the chooser. "Update" and "Ignore" are mutually
/// exclusive: ticking one clears the other.</summary>
public sealed class ScriptFileRow : ReactiveObject
{
    public string Name  { get; }
    public string Kind  { get; }

    [Reactive] public bool Update { get; set; }
    [Reactive] public bool Ignore { get; set; }

    public ScriptFileRow(ScriptFileChange c)
    {
        Name   = c.Name;
        Kind   = c.IsNew ? "new" : "changed";
        Ignore = c.Ignored;
        Update = !c.Ignored;

        this.WhenAnyValue(x => x.Update).Subscribe(v => { if (v && Ignore) Ignore = false; });
        this.WhenAnyValue(x => x.Ignore).Subscribe(v => { if (v && Update) Update = false; });
    }
}

public sealed class ScriptFilesViewModel : ReactiveObject
{
    public string Header { get; }
    public ObservableCollection<ScriptFileRow> Rows { get; }

    public ScriptFilesViewModel(ScriptFilesRequest req)
    {
        Header = $"{req.SourceName} — {req.Files.Count} file(s) differ from your Scripts folder";
        Rows = new(req.Files
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ScriptFileRow(f)));
    }

    public void SetAllUpdate(bool on)
    {
        foreach (var r in Rows) r.Update = on;
    }

    public ScriptFilesResult BuildResult() => new(
        Rows.Where(r => r.Ignore).Select(r => r.Name).ToList(),
        Rows.Where(r => r.Update).Select(r => r.Name).ToList());
}
