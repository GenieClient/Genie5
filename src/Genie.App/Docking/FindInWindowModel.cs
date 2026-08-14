using System.Windows.Input;
using ReactiveUI;

namespace Genie.App.Docking;

/// <summary>A dockable that hosts an in-window Find bar (#120).</summary>
public interface IFindHost
{
    FindInWindowModel Find { get; }
}

/// <summary>
/// Backs the in-window Find bar (#120): a case-insensitive substring search
/// over the window's buffered lines with ▲/▼ navigation. One instance per
/// text window, owned by the tool.
///
/// <para><b>Direction convention:</b> a fresh search selects the NEWEST
/// (bottom-most) match — in a scrolling game log the recent occurrence is
/// almost always the one you want — and Enter / ▲ walks UPWARD through
/// older matches; Shift+Enter / ▼ walks back down. <see cref="Status"/>
/// reads "k of N" with 1 = oldest match.</para>
///
/// <para>The model only computes match positions;
/// <c>Controls.FindInWindow</c> (attached behaviors) owns the visual side —
/// focusing the box when the bar opens, scrolling the hit into view, and
/// selecting the matched range in the line.</para>
/// </summary>
public sealed class FindInWindowModel : ReactiveObject
{
    public readonly record struct Match(int Line, int Col, int Length);

    private readonly Func<IReadOnlyList<string>> _snapshot;
    private readonly List<Match> _matches = new();
    private int    _index = -1;
    private bool   _isOpen;
    private string _findText = "";
    private string _status   = "";

    public FindInWindowModel(Func<IReadOnlyList<string>> linesSnapshot)
    {
        _snapshot    = linesSnapshot;
        OlderCommand = ReactiveCommand.Create(GoOlder);
        NewerCommand = ReactiveCommand.Create(GoNewer);
        CloseCommand = ReactiveCommand.Create(() => { IsOpen = false; });
    }

    /// <summary>Raised when a match should be scrolled into view + selected.</summary>
    public event Action<Match>? JumpRequested;

    /// <summary>
    /// Set by a renderer that ships its own, better find UI — currently only
    /// <c>GameTextEditor</c>, which hands over to AvaloniaEdit's SearchPanel.
    /// Returning true means the renderer took the request and this model's bar
    /// stays shut; false (or no override at all) falls back to the bar.
    ///
    /// <para>The routing decision has to live on the view side: only the control
    /// that actually rendered the window knows which surface is showing it, and
    /// only it can reach the panel. The model just offers the hook so callers
    /// (<see cref="Open"/>) stay renderer-agnostic.</para>
    /// </summary>
    public Func<bool>? OpenOverride { get; set; }

    /// <summary>Open this window's find UI — the renderer's own if it has one
    /// (<see cref="OpenOverride"/>), otherwise the in-window bar. Every
    /// user-facing entry point that should prefer the native UI goes through
    /// here; setting <see cref="IsOpen"/> directly opts out and forces the bar.</summary>
    public void Open()
    {
        if (OpenOverride?.Invoke() == true) return;
        IsOpen = true;
    }

    /// <summary>Walk to the previous (older, upward) match.</summary>
    public ICommand OlderCommand { get; }
    /// <summary>Walk to the next (newer, downward) match.</summary>
    public ICommand NewerCommand { get; }
    public ICommand CloseCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;
            this.RaiseAndSetIfChanged(ref _isOpen, value);
            if (value) Recompute(jump: _findText.Length > 0);
            else Status = "";
        }
    }

    public string FindText
    {
        get => _findText;
        set
        {
            if (_findText == value) return;
            this.RaiseAndSetIfChanged(ref _findText, value);
            Recompute(jump: true);
        }
    }

    /// <summary>"k of N", "no matches", or "" when idle.</summary>
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Re-scan the buffer (first occurrence per line — enough to land the
    /// eye on the right row). Selects the newest match. Re-run on every term
    /// change; navigation reuses the last scan (the buffer may have grown a
    /// few lines since, which only shifts what "newest" means — an Up/Down
    /// press after new text lands still walks sensibly).
    /// </summary>
    private void Recompute(bool jump)
    {
        _matches.Clear();
        _index = -1;

        var term = _findText.Trim();
        if (!_isOpen || term.Length == 0)
        {
            Status = "";
            return;
        }

        var lines = _snapshot();
        for (var i = 0; i < lines.Count; i++)
        {
            var col = lines[i]?.IndexOf(term, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (col >= 0) _matches.Add(new Match(i, col, term.Length));
        }

        if (_matches.Count == 0)
        {
            Status = "no matches";
            return;
        }

        _index = _matches.Count - 1;   // newest
        UpdateStatus();
        if (jump) JumpRequested?.Invoke(_matches[_index]);
    }

    private void GoOlder() => Step(-1);
    private void GoNewer() => Step(+1);

    private void Step(int delta)
    {
        if (_matches.Count == 0)
        {
            Recompute(jump: true);
            return;
        }
        _index = (_index + delta + _matches.Count) % _matches.Count;
        UpdateStatus();
        JumpRequested?.Invoke(_matches[_index]);
    }

    private void UpdateStatus() => Status = $"{_index + 1} of {_matches.Count}";
}
