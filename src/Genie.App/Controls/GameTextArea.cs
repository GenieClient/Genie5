using System;
using System.Linq;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace Genie.App.Controls;

/// <summary>
/// The <see cref="TextArea"/> behind the Game window's editor renderer, made
/// transparent to editing input.
///
/// <para>AvaloniaEdit swallows typing whether or not the editor is read-only:
/// <c>TextArea.OnTextInput</c> marks the event handled after calling
/// <c>PerformTextInput</c>, and the Backspace/Delete/Enter/arrow key bindings
/// mark their <c>KeyDown</c> handled from the command handler. <c>IsReadOnly</c>
/// only stops the document from changing — it does not stop the event from
/// being consumed.</para>
///
/// <para>That silently broke type-anywhere (#141). <c>MainWindow</c> listens for
/// <c>TextInput</c> on the bubble route with <c>handledEventsToo: false</c> and
/// redirects printable text into the command bar; with the TextArea focused it
/// never sees the keystroke, so typing after a click in the Game window did
/// nothing at all. The legacy renderer has no editing input to begin with, which
/// is why the behaviour only disappeared under the flag.</para>
///
/// <para>Nothing suppressed here is behaviour worth keeping: the view is
/// read-only, so every one of these commands was already a no-op on the
/// document — apart from the <c>BringCaretToView()</c> that ends the delete and
/// enter handlers (which yanks a scrolled-back view down to the caret) and the
/// arrow keys' own caret-move/scroll, none of which matters when nothing is
/// ever typed into this view.</para>
/// </summary>
internal sealed class GameTextArea : TextArea
{
    /// <summary>Keys whose editing bindings are dropped so the keystroke keeps
    /// bubbling to <c>MainWindow</c>'s forward-to-command-bar handler:
    /// Backspace is the other half of #141 (it produces no <c>TextInput</c>,
    /// so it has its own redirect); Delete, Enter and the arrow keys are
    /// removed so history recall (Up/Down) and submit (Enter) reach the
    /// command bar even when the Game window has focus, same reasoning as
    /// the caret-scroll suppression above. All modifier variants of these
    /// keys go with them (e.g. Ctrl+Left word-jump) — read-only, so none of
    /// it was doing anything worth keeping anyway.</summary>
    private static readonly Key[] SuppressedEditingKeys =
        [Key.Back, Key.Delete, Key.Enter, Key.Up, Key.Down, Key.Left, Key.Right];

    public GameTextArea()
    {
        // Both the Editing and CaretNavigation nested handlers copy their
        // own static binding lists into per-instance copies on creation
        // (EditingCommandHandler.Create / CaretNavigationCommandHandler.Create),
        // so pruning either here affects this TextArea only.
        //
        // Two passes are needed, not one. Most of what we're suppressing —
        // EditingCommands.Delete/Backspace/EnterParagraphBreak, and every
        // EditingCommands.Move*/Select* the arrow keys drive — are bare
        // RoutedCommands with no gesture of their own; they fire purely
        // through the handler's KeyBindings list, which the first loop
        // below strips (arrow-key caret movement lives in the separate
        // CaretNavigation handler, not Editing, hence looping over both).
        // But AvaloniaEdit's ApplicationCommands.Delete is different: it's
        // defined with its own baked-in `new KeyGesture(Key.Delete)`, and
        // TextAreaInputHandler.TextAreaOnKeyDown checks CommandBindings
        // against each command's own Gesture as a SEPARATE pass, after the
        // KeyBindings pass — so removing only the KeyBindings entry left
        // Delete still swallowed by that second pass (#141 follow-up: Delete
        // wasn't reaching the command bar even though Backspace/Enter, which
        // have no such gesture-bearing command, worked fine). The second
        // loop below removes any CommandBindings entry whose command has a
        // matching baked-in gesture too. Copy/Cut/Paste use the same
        // gesture-bearing-command pattern but aren't in SuppressedEditingKeys,
        // so they're untouched.
        foreach (var handler in new[] { DefaultInputHandler.Editing, DefaultInputHandler.CaretNavigation })
        {
            foreach (var binding in handler.KeyBindings
                         .Where(b => b.Gesture is { } gesture && SuppressedEditingKeys.Contains(gesture.Key))
                         .ToList())
                handler.KeyBindings.Remove(binding);

            foreach (var binding in handler.CommandBindings
                         .Where(b => b.Command.Gesture is { } gesture && SuppressedEditingKeys.Contains(gesture.Key))
                         .ToList())
                handler.CommandBindings.Remove(binding);
        }
    }

    /// <summary>Take the stock <see cref="TextArea"/> control theme; Avalonia keys
    /// themes off the concrete type, and AvaloniaEdit's is keyed
    /// <c>{x:Type editing:TextArea}</c>.</summary>
    protected override Type StyleKeyOverride => typeof(TextArea);

    /// <summary>Leave printable input for the window's type-anywhere handler.
    /// Deliberately does not call <c>base</c>: <see cref="TextArea"/>'s
    /// implementation is what marks the event handled. The base-base
    /// (<c>InputElement.OnTextInput</c>) is empty, so nothing is lost.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
    }
}

/// <summary>
/// A <see cref="TextEditor"/> hosting a <see cref="GameTextArea"/>. The
/// constructor that injects a TextArea is <c>protected</c>, so a subclass is the
/// only way in.
/// </summary>
internal sealed class GameTextEditorControl : TextEditor
{
    public GameTextEditorControl() : base(new GameTextArea())
    {
    }

    /// <summary>As <see cref="GameTextArea.StyleKeyOverride"/> — keep the stock
    /// <see cref="TextEditor"/> theme.</summary>
    protected override Type StyleKeyOverride => typeof(TextEditor);
}
