using System.Text.RegularExpressions;
using Genie.Core.Classes;
using Genie.Core.Commanding;

namespace Genie.Core.Triggers;

public sealed class TriggerEngineFinal
{
    private readonly List<TriggerRule> _triggers = new();

    // Copy-on-write iteration snapshot. ProcessLine dispatches actions from
    // inside its own loop, and an action is free to edit the trigger list: the
    // community idiom `#trigger {stow crossbow} {…;#trigger remove {stow
    // crossbow}}` removes the very rule being fired. Enumerating the live List
    // there throws "Collection was modified" deterministically. Rule edits also
    // arrive off the pipeline thread — the Configuration panel, `#trigger add`,
    // and rule-file live reload's clear-then-rebuild, which is exactly what a
    // player editing triggers.json mid-combat does.
    //
    // The sibling engines (highlights, gags, substitutes) were given this same
    // treatment with the game-thread work; the trigger engine was the one left
    // on a live List. ScriptEngine.FireActions snapshots for the same reason.
    //
    // Semantics, deliberately: a rule removed by an action still fires for the
    // line in flight, and one added by an action does not. The set is fixed when
    // the line arrives, which is the only reading that stays stable while the
    // list is being rewritten underneath.
    private volatile TriggerRule[] _snapshot = Array.Empty<TriggerRule>();
    private void Resnap() => _snapshot = _triggers.ToArray();

    private readonly ICommandHost?     _host;
    private readonly CommandEngine?    _commandEngine;

    /// <summary>
    /// Construct with optional host + command engine. Both are only used when
    /// triggers fire (<see cref="ProcessLine"/>), so offline / draft instances
    /// for the Configuration dialog can pass nulls.
    /// </summary>
    public TriggerEngineFinal(ICommandHost? host = null, CommandEngine? commandEngine = null)
    { _host = host; _commandEngine = commandEngine; }

    public IReadOnlyList<TriggerRule> Triggers => _triggers;
    public ClassEngine? Classes { get; set; }

    /// <summary>Master enable (File ▸ Master Toggles / <c>#config triggers</c>).
    /// When off, <see cref="ProcessLine"/> fires nothing — rules stay loaded.</summary>
    public bool Enabled { get; set; } = true;

    private bool _safetyEnabled = true;
    /// <summary>When true, trigger regexes run with a match-timeout + literal
    /// pre-filter — the main guard against a catastrophic user pattern freezing
    /// the read thread. Toggling rebuilds every trigger's regex.</summary>
    public bool SafetyEnabled
    {
        get => _safetyEnabled;
        set { if (_safetyEnabled == value) return; _safetyEnabled = value; foreach (var t in _triggers) t.Rebuild(value); }
    }

    public TriggerRule AddTrigger(string pattern, string action, bool caseSensitive = false,
                                  bool isEnabled = true, string className = "", string soundFile = "",
                                  string speak = "", bool eval = false, bool matchAll = false)
    {
        var trigger = new TriggerRule(pattern, action, caseSensitive, isEnabled, className, _safetyEnabled, soundFile, speak, eval, matchAll);
        _triggers.Add(trigger);
        Resnap();
        if (!string.IsNullOrEmpty(className)) Classes?.Ensure(className);
        return trigger;
    }

    public bool RemoveTrigger(string pattern)
    {
        var removed = _triggers.RemoveAll(t => t.Pattern == pattern) > 0;
        if (removed) Resnap();
        return removed;
    }

    public void Clear() { _triggers.Clear(); Resnap(); }

    public bool SetEnabled(string pattern, bool isEnabled)
    {
        var t = _triggers.FirstOrDefault(t => t.Pattern == pattern);
        if (t is null) return false;
        t.IsEnabled = isEnabled;
        return true;
    }

    public void ProcessLine(string line, bool echoTriggerDebug = true)
    {
        if (!Enabled) return;
        foreach (var trigger in _snapshot)
        {
            if (!trigger.IsEnabled) continue;
            if (Classes is not null && !Classes.IsActive(trigger.ClassName)) continue;

            if (trigger.MatchAll)
            {
                // #23 — fire the action once per match, each with its own $0..$n.
                var matches = trigger.SafeMatchAll(line);
                if (matches.Count == 0) continue;
                // Alerts (sound + speak) are line-level notifications, so they
                // fire ONCE per line (on the first match); only the action repeats.
                FireAlerts(trigger, line, matches[0]);
                foreach (var m in matches) FireAction(trigger, m);
            }
            else
            {
                if (trigger.SafeMatch(line) is not { } match) continue;
                FireAlerts(trigger, line, match);
                FireAction(trigger, match);
            }
        }
    }

    /// <summary>Per-trigger SFX + TTS (line-level alerts). Sound rides the host's
    /// PlaySounds gate; "*" speaks the whole matched line, anything else the text
    /// with $0..$n expanded, urgent so it barges in over stream read-aloud.</summary>
    private void FireAlerts(TriggerRule trigger, string line, Match match)
    {
        if (!string.IsNullOrEmpty(trigger.SoundFile))
            _host?.PlaySound(trigger.SoundFile);
        if (!string.IsNullOrEmpty(trigger.Speak))
            _host?.Speak(trigger.Speak == "*" ? line : ExpandAction(trigger.Speak, match), urgent: true);
    }

    /// <summary>Expand $0..$n (and, opt-in #150, evaluate {…} blocks), then
    /// dispatch the action. Automated — not interactive, so a #var/#tvar in the
    /// action sets silently instead of echoing "Variable set:".</summary>
    private void FireAction(TriggerRule trigger, Match match)
    {
        var expandedAction = ExpandAction(trigger.Action, match);
        if (trigger.Eval) expandedAction = EvalBraces(expandedAction);
        _commandEngine?.ProcessInput(expandedAction, interactive: false);
    }

    private static string ExpandAction(string action, Match match)
    {
        var result = action;
        for (var i = match.Groups.Count - 1; i >= 0; i--)
            result = result.Replace("$" + i, match.Groups[i].Value);
        return result;
    }

    /// <summary>
    /// Evaluate <c>{…}</c> expression blocks in a trigger action (#150). Resolves
    /// innermost-first so nested blocks compute inside-out, and expands any
    /// <c>$variables</c> inside a block (via the host) before evaluating — the
    /// same order as <c>#eval</c> (variables first, then the expression). A block
    /// that fails to evaluate is left as its literal inner text (braces dropped)
    /// rather than aborting the whole action. Runs only for eval-flagged rules.
    /// </summary>
    private string EvalBraces(string action)
    {
        // Bounded loop: each pass collapses one innermost {…}; the guard caps
        // pathological input (unbalanced braces stop the loop naturally).
        for (int guard = 0; guard < 100; guard++)
        {
            int close = action.IndexOf('}');
            if (close < 0) break;
            int open = action.LastIndexOf('{', close);
            if (open < 0) break;                     // stray '}' — leave the rest literal
            var inner    = action.Substring(open + 1, close - open - 1);
            var expanded = _host?.ExpandVariables(inner) ?? inner;
            string value;
            try   { value = Scripting.ScriptExpression.EvalString(expanded, new Scripting.ScriptInstance()); }
            catch { value = expanded; }              // malformed expr → literal inner text
            action = action[..open] + value + action[(close + 1)..];
        }
        return action;
    }
}
