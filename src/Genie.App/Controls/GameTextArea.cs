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
/// <c>PerformTextInput</c>, and the Backspace/Delete/Enter key bindings mark
/// their <c>KeyDown</c> handled from the command handler. <c>IsReadOnly</c> only
/// stops the document from changing — it does not stop the event from being
/// consumed.</para>
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
/// enter handlers, which yanks a scrolled-back view down to the caret.</para>
/// </summary>
internal sealed class GameTextArea : TextArea
{
    /// <summary>Keys whose editing bindings are dropped so the keystroke keeps
    /// bubbling to <c>MainWindow</c>: Backspace is the other half of #141
    /// (it produces no <c>TextInput</c>, so it has its own redirect), Delete and
    /// Enter are removed for the caret-scroll reason above.</summary>
    private static readonly Key[] SuppressedEditingKeys = [Key.Back, Key.Delete, Key.Enter];

    public GameTextArea()
    {
        // The Editing handler copies the shared static binding list into its own
        // on creation (EditingCommandHandler.Create), so pruning it here affects
        // this TextArea only. Copy/Cut/Paste are not key bindings — they are
        // matched from CommandBindings against the platform gesture — so Cmd+C /
        // Ctrl+C is untouched.
        foreach (var binding in DefaultInputHandler.Editing.KeyBindings
                     .Where(b => b.Gesture is { } gesture && SuppressedEditingKeys.Contains(gesture.Key))
                     .ToList())
            DefaultInputHandler.Editing.KeyBindings.Remove(binding);
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
