using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Genie.Core;
using ReactiveUI;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the Raw XML dock tool (issue #14): a live, read-only view of the
/// raw server XML stream exactly as it arrives off the wire — the same bytes
/// the parser sees, before any tag stripping. Useful for parser development,
/// "where did that line come from?" debugging, and learning the DR protocol.
///
/// Local view only: it never sends anywhere, just mirrors the stream into a
/// capped rolling buffer with auto-scroll. Default hidden (opt-in via
/// Window → Raw XML).
///
/// Lifecycle: subscribes to <see cref="GenieCore.RawXmlStream"/> — the
/// persistent relay that survives reconnects — so a single Attach (once per
/// app session, from <see cref="MainWindowViewModel"/>) keeps feeding the
/// panel across disconnect/reconnect with no re-wiring. Every UI-bound mutation
/// marshals onto the UI thread via <see cref="RxApp.MainThreadScheduler"/>
/// because raw chunks arrive on the connection read-loop thread.
/// </summary>
public class RawXmlViewModel : ReactiveObject, Controls.IScrollHoldSink
{
    /// <summary>Rolling cap so a long session can't grow the buffer without
    /// bound. The raw stream is higher-volume than the parsed feeds, so this
    /// is generous but still finite.</summary>
    private const int MaxLines = 5000;

    // Scroll-hold trim deferral (#293 follow-up) — see GameTextViewModel.ViewHeld.
    private bool _viewHeld;
    public bool ViewHeld
    {
        get => _viewHeld;
        set
        {
            if (_viewHeld == value) return;
            _viewHeld = value;
            if (!value)
                while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
    }

    private int TrimCap => _viewHeld ? MaxLines * 2 : MaxLines;

    /// <summary>One raw line per row, as a plain <c>TextLine</c> (Color =
    /// StreamColor.Main, no Links/BoldSpans/PresetSpans) — this is a verbatim
    /// protocol dump, so no highlighting/inlines happen. Unified with
    /// GameTextViewModel/StreamBuffer's shape so the AvaloniaEdit renderer
    /// (GameTextEditor) can bind against any of the three without a special
    /// case (#296).</summary>
    public ObservableCollection<TextLine> Lines { get; } = [];

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public RawXmlViewModel()
    {
        ClearCommand = ReactiveCommand.Create(() => Lines.Clear());
    }

    /// <summary>
    /// Bind to the live persistent core. Called once per app session by
    /// <see cref="MainWindowViewModel.WireCore"/>; the subscription lives for
    /// the whole session and rides reconnects via the relay.
    /// </summary>
    public void Attach(GenieCore core)
    {
        Lines.Clear();

        core.RawXmlStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(AddChunk, static _ => { });
    }

    /// <summary>
    /// A single emission off the wire may carry several newline-delimited
    /// lines (or none). Split so the rolling cap counts real lines and each
    /// row is one protocol line; trailing CR is stripped, and lines that are
    /// nothing but whitespace are dropped as visual noise.
    /// </summary>
    private void AddChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        foreach (var raw in chunk.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            Lines.Add(new TextLine(line, StreamColor.Main));
            if (Lines.Count > TrimCap)
                Lines.RemoveAt(0);
        }
    }
}
