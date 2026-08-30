using System.Collections.ObjectModel;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs a <b>plugin- or script-created</b> dock panel. Unlike
/// <see cref="ExperienceViewModel"/> (hard-wired to the "Experience" window),
/// this VM is generic: one instance per distinct window name written to via
/// <c>IPluginHost.SetWindow(name, …)</c>, <c>#echo &gt;Name</c>, or
/// <c>#link &gt;Name</c>. The dock factory creates a panel on demand and binds
/// it here, so any plugin or menu script can surface its own window without the
/// App knowing about it in advance.
///
/// <para>Line-based (an <see cref="ObservableCollection{T}"/> of
/// <see cref="TextLine"/>) so the panel renders through the same
/// link-capable pipeline the game/stream windows use — that's what lets a
/// Genie 4 <c>#link</c> menu line be clickable here, not just in Main.</para>
/// </summary>
public class PluginWindowViewModel : ReactiveObject, Controls.IScrollHoldSink
{
    private const int Max = 2000;

    // Scroll-hold trim deferral (#293 follow-up) — see GameTextViewModel.ViewHeld.
    private bool _viewHeld;
    public bool ViewHeld
    {
        get => _viewHeld;
        set
        {
            if (_viewHeld == value) return;
            _viewHeld = value;
            if (!value) Trim();
        }
    }

    private int TrimCap => _viewHeld ? System.Math.Max(Max * 2, Max + 1000) : Max;

    public PluginWindowViewModel(string title)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Plugin" : title;
    }

    /// <summary>Display title — drives the dock tab caption. A plugin may rename
    /// its window by writing under a new name (a new VM) or by the factory
    /// updating this when it reuses the VM.</summary>
    [Reactive] public string Title { get; set; }

    /// <summary>The panel's lines. Rendered via <see cref="TextLine.Inlines"/>, so
    /// clickable <c>#link</c> lines, user highlights, and <c>&lt;d cmd&gt;</c>
    /// links all work here exactly as in the stream panels.</summary>
    public ObservableCollection<TextLine> Lines { get; } = new();

    /// <summary>Replace the whole panel (plugin <c>SetWindow</c> semantics).
    /// Splits on newlines so multi-line plugin output (inventory trees, tables)
    /// keeps one line per row.</summary>
    public void SetContent(string content)
    {
        Lines.Clear();
        if (string.IsNullOrEmpty(content)) return;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            Lines.Add(new TextLine(line, StreamColor.Main, Window: Title));
        Trim();
    }

    /// <summary>Append a plain line (<c>EchoToWindow</c> / <c>#echo &gt;Name</c>).
    /// Keeps the panel growing like a log.</summary>
    public void AppendLine(string text)
    {
        Lines.Add(new TextLine(text ?? "", StreamColor.Main, Window: Title));
        Trim();
    }

    /// <summary>Append a clickable Genie 4 <c>#link</c> line — the whole line is a
    /// link that runs <paramref name="command"/> (via the normal link-click →
    /// ProcessInput path) when clicked.</summary>
    public void AppendLink(string text, string command)
    {
        Lines.Add(new TextLine(text, StreamColor.Main,
                               Links: new[] { new LinkSpan(0, text.Length, command) }, Window: Title));
        Trim();
    }

    /// <summary>Empty the panel (<c>#clear &gt;Name</c>, or the owning plugin
    /// being disabled).</summary>
    public void Clear() => Lines.Clear();

    private void Trim()
    {
        while (Lines.Count > TrimCap) Lines.RemoveAt(0);
    }
}
