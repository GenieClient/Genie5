using System;
using System.IO;
using Genie.Core.Scripting;

namespace Genie.App.ScriptEditing;

/// <summary>Where an edit request went.</summary>
public enum ScriptEditRoute { BuiltIn, External }

/// <summary>
/// Decides where a script edit opens (public #243) — the built-in editor, or the
/// external-editor ladder when <c>#config externaleditor on</c> — and vets
/// <c>#edit</c> names. Kept apart from <c>MainWindowViewModel</c> so the routing
/// and the path rules are testable without a window or a live core.
/// </summary>
public sealed class ScriptEditRouter
{
    private readonly Func<bool> _useExternal;
    private readonly Action<string> _openBuiltIn;
    private readonly Action<string> _openExternal;

    /// <param name="useExternal">Read at every request, so a <c>#config</c> change
    /// applies to the next edit.</param>
    /// <param name="openBuiltIn">Opens (or focuses) the built-in editor.</param>
    /// <param name="openExternal">Launches the external-editor ladder.</param>
    public ScriptEditRouter(Func<bool> useExternal, Action<string> openBuiltIn, Action<string> openExternal)
    {
        _useExternal  = useExternal;
        _openBuiltIn  = openBuiltIn;
        _openExternal = openExternal;
    }

    /// <summary>Open <paramref name="path"/> where the settings say; returns which.
    /// Exceptions from the opener propagate to the caller, which reports them.</summary>
    public ScriptEditRoute Open(string path)
    {
        if (_useExternal())
        {
            _openExternal(path);
            return ScriptEditRoute.External;
        }
        _openBuiltIn(path);
        return ScriptEditRoute.BuiltIn;
    }

    /// <summary>
    /// True when <paramref name="name"/> is an acceptable <c>#edit</c> script name:
    /// a bare name under the Scripts folder — no path separators, no <c>..</c>,
    /// nothing rooted or drive-qualified.
    /// </summary>
    public static bool IsValidName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(['/', '\\']) < 0
           && !name.Contains("..")
           && !Path.IsPathRooted(name);

    /// <summary>
    /// The existing file <c>#edit <paramref name="name"/></c> opens: the one a
    /// start of that name would run (<see cref="ScriptEngine.ResolveScriptFile"/>
    /// — the default extension, then .cmd / .inc / .js, Scripts folder before the
    /// repo-scripts folder), and only when it lies inside those folders. Null when
    /// the name is invalid or nothing matches (the caller may then create it).
    /// </summary>
    public static string? ResolveExisting(ScriptEngine engine, string name)
    {
        name = name.Trim();
        if (!IsValidName(name)) return null;
        var path = engine.ResolveScriptFile(name);
        if (path is null || !engine.IsUnderScriptRoots(path)) return null;
        return Path.GetFullPath(path);
    }
}
