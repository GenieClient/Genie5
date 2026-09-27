using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Xml;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Genie.App.ScriptEditing;

/// <summary>
/// Syntax definitions for the built-in script editor (public #243): Genie
/// <c>.cmd</c> / <c>.inc</c> from our own <c>GenieCmd.xshd</c>, JavaScript from
/// the definition AvaloniaEdit ships. Both are loaded at the XSHD level and every
/// named colour is repainted from a palette for the current theme variant before
/// the definition is built — AvaloniaEdit's stock colours are pure blue and dark
/// green, made for a white page and near-unreadable on the dark themes.
/// </summary>
public static class ScriptSyntax
{
    /// <summary>Manifest name of the <c>.cmd</c> definition in this assembly.</summary>
    public const string CmdResourceName = "Genie.App.ScriptEditing.GenieCmd.xshd";

    /// <summary>Manifest name of AvaloniaEdit's own JavaScript definition.</summary>
    public const string JsResourceName = "AvaloniaEdit.Highlighting.Resources.JavaScript-Mode.xshd";

    private static readonly ConcurrentDictionary<(bool Js, bool Dark), IHighlightingDefinition> Cache = new();

    /// <summary>The definition for <paramref name="path"/>'s extension (null for
    /// a file type we don't colour), painted for a dark or light background.</summary>
    public static IHighlightingDefinition? ForFile(string path, bool dark)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        bool? js = ext switch
        {
            ".js"  => true,
            ".cmd" or ".inc" => false,
            _ => null,
        };
        return js is null ? null : Get(js.Value, dark);
    }

    /// <summary>The <c>.cmd</c> (<paramref name="js"/> false) or JavaScript definition.</summary>
    public static IHighlightingDefinition Get(bool js, bool dark)
        => Cache.GetOrAdd((js, dark), key =>
        {
            var xshd = key.Js ? LoadJsXshd() : LoadCmdXshd();
            ApplyPalette(xshd, key.Dark);
            return HighlightingLoader.Load(xshd, HighlightingManager.Instance);
        });

    /// <summary>Parse the shipped <c>.cmd</c> definition (unpainted).</summary>
    public static XshdSyntaxDefinition LoadCmdXshd()
        => LoadXshd(typeof(ScriptSyntax).Assembly, CmdResourceName);

    /// <summary>Parse AvaloniaEdit's JavaScript definition (unpainted).</summary>
    public static XshdSyntaxDefinition LoadJsXshd()
        => LoadXshd(typeof(HighlightingManager).Assembly, JsResourceName);

    private static XshdSyntaxDefinition LoadXshd(System.Reflection.Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing syntax definition resource '{name}'.");
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.LoadXshd(reader);
    }

    /// <summary>Palette roles a named colour maps to.</summary>
    public enum Role { Comment, String, Label, Variable, Keyword, MetaCommand, Number, Function }

    /// <summary>The role for a named colour in either definition — ours use the
    /// role names directly; AvaloniaEdit's JavaScript names are mapped.</summary>
    public static Role RoleFor(string colorName) => colorName switch
    {
        "Comment"                                    => Role.Comment,
        "String" or "Character" or "Regex"           => Role.String,
        "Label"                                      => Role.Label,
        "Variable"                                   => Role.Variable,
        "MetaCommand"                                => Role.MetaCommand,
        "Number" or "Digits"                         => Role.Number,
        "JavaScriptIntrinsics" or "JavaScriptGlobalFunctions" => Role.Function,
        _                                            => Role.Keyword,
    };

    /// <summary>Colour for <paramref name="role"/> on a dark or light background.
    /// Both sets clear 4.5:1 contrast against the Fluent window backgrounds.</summary>
    public static Color ColorFor(Role role, bool dark) => (role, dark) switch
    {
        (Role.Comment, true)      => Color.Parse("#6A9955"),
        (Role.Comment, false)     => Color.Parse("#007A00"),
        (Role.String, true)       => Color.Parse("#CE9178"),
        (Role.String, false)      => Color.Parse("#A31515"),
        (Role.Label, true)        => Color.Parse("#DCDCAA"),
        (Role.Label, false)       => Color.Parse("#795E26"),
        (Role.Variable, true)     => Color.Parse("#9CDCFE"),
        (Role.Variable, false)    => Color.Parse("#001080"),
        (Role.Keyword, true)      => Color.Parse("#569CD6"),
        (Role.Keyword, false)     => Color.Parse("#0000FF"),
        (Role.MetaCommand, true)  => Color.Parse("#C586C0"),
        (Role.MetaCommand, false) => Color.Parse("#AF00DB"),
        (Role.Number, true)       => Color.Parse("#B5CEA8"),
        (Role.Number, false)      => Color.Parse("#098658"),
        (Role.Function, true)     => Color.Parse("#DCDCAA"),
        _                         => Color.Parse("#795E26"),
    };

    /// <summary>Repaint every named colour in <paramref name="xshd"/>.</summary>
    public static void ApplyPalette(XshdSyntaxDefinition xshd, bool dark)
    {
        foreach (var color in xshd.Elements.OfType<XshdColor>())
        {
            if (string.IsNullOrEmpty(color.Name)) continue;
            color.Foreground = new SimpleHighlightingBrush(ColorFor(RoleFor(color.Name), dark));
        }
    }
}
