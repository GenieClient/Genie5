using Genie.Core.Classes;
using Genie.Core.Commanding;

namespace Genie.Core.Aliases;

public sealed class AliasEngine
{
    private readonly List<AliasRule>  _aliases = new();
    private readonly CommandEngine?   _commandEngine;

    /// <summary>
    /// Command engine is only used when an alias fires (<see cref="TryProcess"/>).
    /// Offline / draft instances for the Configuration dialog can pass null.
    /// </summary>
    public AliasEngine(CommandEngine? commandEngine = null) { _commandEngine = commandEngine; }

    /// <summary>
    /// Optional class-scope filter — set by <see cref="GenieCore"/> at startup.
    /// When non-null, aliases only fire if their <see cref="AliasRule.ClassName"/>
    /// is active. When null (e.g. offline draft instances in the Configuration
    /// dialog), every enabled alias fires regardless of class state.
    /// </summary>
    public ClassEngine? Classes { get; set; }

    public IReadOnlyList<AliasRule> Aliases => _aliases;

    /// <summary>Master enable (File ▸ Master Toggles / <c>#config aliases</c>).
    /// When off, <see cref="TryProcess"/> expands nothing — input passes through
    /// as typed. Rules stay loaded and editable.</summary>
    public bool Enabled { get; set; } = true;

    public AliasRule AddAlias(string name, string expansion, bool isEnabled = true, string className = "default")
    { var a = new AliasRule(name, expansion, isEnabled, className); _aliases.Add(a); return a; }

    public bool RemoveAlias(string name) => _aliases.RemoveAll(a => a.Name == name) > 0;
    public void Clear() => _aliases.Clear();

    public bool SetEnabled(string name, bool enabled)
    {
        var alias = _aliases.FirstOrDefault(a => a.Name == name);
        if (alias == null) return false;
        alias.IsEnabled = enabled;
        return true;
    }

    /// <summary>
    /// Matches <paramref name="input"/> against the alias list and, on a hit,
    /// runs the expansion with Genie 4's argument substitution applied.
    /// </summary>
    /// <remarks>
    /// Genie 4 parity (<c>Core/Command.cs ParseAlias</c>):
    /// <list type="bullet">
    ///   <item><c>$0</c> — the whole argument string, everything after the
    ///     alias name.</item>
    ///   <item><c>$1</c>, <c>$2</c>, … — positional arguments; an argument
    ///     the caller did not supply substitutes as empty.</item>
    ///   <item>An expansion containing no <c>$</c> at all gets the argument
    ///     string appended, so <c>#alias {gr} {get rope}</c> turns
    ///     <c>gr from pack</c> into <c>get rope from pack</c>.</item>
    /// </list>
    /// Only <c>$*</c> was implemented before, so every numbered argument
    /// reached the game as the literal text <c>$1</c> — 94 of the 356 aliases
    /// in the reference settings tree use them.
    /// </remarks>
    public bool TryProcess(string input)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        var parts   = trimmed.Split(' ', 2);
        var alias = _aliases.FirstOrDefault(a =>
            a.IsEnabled
            && (Classes?.IsActive(a.ClassName) ?? true)
            && a.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (alias == null) return false;

        var args = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        if (_commandEngine is null) return true;
        // Name the origin so `#config tracesends` can attribute the send (#306).
        using var _ = _commandEngine.PushOrigin("alias");
        _commandEngine.ProcessInput(Expand(alias.Expansion, trimmed, args));
        return true;
    }

    /// <summary>
    /// Apply <c>$*</c>, <c>$0</c> and <c>$n</c> substitution, or append the
    /// argument string when the expansion takes no arguments.
    /// </summary>
    internal static string Expand(string expansion, string wholeInput, string args)
    {
        if (!expansion.Contains('$'))
            return args.Length == 0 ? expansion : expansion + " " + args;

        // Tokenized form of the WHOLE line, so tokens[0] is the alias name and
        // tokens[1] is $1 — the indexing Genie 4 uses. ArgumentParser is the
        // port of Genie 4's Utility.ParseArgs, so {braced} and "quoted"
        // arguments group and lose their wrapper exactly as they did there.
        var tokens = Parsing.ArgumentParser.ParseArgs(wholeInput);

        var expanded = expansion.Replace("$*", args);

        // One pass over $N so that $10 resolves as argument 10. Genie 4 looped
        // ascending and replaced $1 inside $10 first, leaving "<arg1>0"; that
        // made anything past $9 unusable rather than meaningful, so this is a
        // deliberate, documented divergence rather than a parity break.
        return ArgPattern.Replace(expanded, m =>
        {
            var index = int.Parse(m.Groups[1].Value);
            if (index == 0) return args;
            return index < tokens.Count ? tokens[index] : string.Empty;
        });
    }

    private static readonly System.Text.RegularExpressions.Regex ArgPattern =
        new(@"\$(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
}
