using System.Collections.ObjectModel;
using System.Reactive;
using Genie.Core.Commanding;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

public class CommandViewModel : ReactiveObject
{
    private readonly Func<string, Task> _send;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;

    [Reactive] public string CommandText { get; set; } = "";

    public ReactiveCommand<Unit, Unit> SubmitCommand { get; }

    public CommandViewModel(Func<string, Task> send)
    {
        _send = send;
        SubmitCommand = ReactiveCommand.CreateFromTask(Submit,
            this.WhenAnyValue(x => x.CommandText, t => !string.IsNullOrWhiteSpace(t)));
    }

    private async Task Submit()
    {
        var cmd = CommandText.Trim();
        if (string.IsNullOrEmpty(cmd)) return;

        // Store a password-masked copy in the recall history so an explicit
        // `#connect account password character game` can't be retrieved in
        // plaintext via Up-arrow. The real (unmasked) line is still sent.
        _history.Add(ConnectCommandMask.Mask(cmd));
        _historyIndex = -1;
        CommandText = "";

        await _send(cmd);
    }

    public void HistoryUp()
    {
        if (_history.Count == 0) return;
        if (_historyIndex < 0) _historyIndex = _history.Count;
        if (_historyIndex > 0)
        {
            _historyIndex--;
            CommandText = _history[_historyIndex];
        }
    }

    /// <summary>
    /// Down arrow: walk forward through recall history, or — when not mid-recall
    /// — clear the input (#262).
    ///
    /// <para>
    /// The clear is Genie 4 behaviour, added there on request and marked as such:
    /// <c>ComponentTextBox.KeyDownHistory</c>'s final branch is
    /// <c>else // On Request from Fatal (Down Clears)</c>, reached exactly when
    /// <c>HistoryPos == -1</c> — not recalling. Genie 5 returned early in that
    /// case instead, so Down did nothing on a freshly typed line and there was no
    /// key that cleared the box at all (Esc is the script / auto-walk kill
    /// switch and can't be borrowed for it).
    /// </para>
    ///
    /// <para>
    /// One deliberate divergence: Genie 4 guards the whole method with
    /// <c>if (HistoryArray.Count == 0) return;</c>, so in a session where you
    /// have not yet sent anything, Down does nothing there. That guard sits
    /// ahead of the clear branch, which reads as an ordering artefact rather
    /// than intent — and honouring it would mean Down silently doing nothing for
    /// anyone testing the fix on a fresh session. We clear whenever the user is
    /// not mid-recall, empty history included.
    /// </para>
    /// </summary>
    public void HistoryDown()
    {
        if (_historyIndex < 0) { CommandText = ""; return; }

        _historyIndex++;
        CommandText = _historyIndex < _history.Count ? _history[_historyIndex] : "";
        if (_historyIndex >= _history.Count) _historyIndex = -1;
    }
}
