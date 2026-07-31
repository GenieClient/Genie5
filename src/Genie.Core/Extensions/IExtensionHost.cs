namespace Genie.Core.Extensions;

public interface IExtensionHost
{
    IDictionary<string, string> Globals { get; }
    void Echo(string text);
    void SendCommand(string command);

    /// <summary>Replace a named dock-panel's entire contents (snapshot-style — the
    /// tracker re-renders its whole list each update). The App surfaces unknown
    /// window names as dock panels; this is the same seam plugins used via
    /// <c>IPluginHost.SetWindow</c>, so the App needs no per-tracker wiring.</summary>
    void SetWindow(string window, string content);

    /// <summary>Absolute path to the active config directory (per-character profile
    /// when one is in effect), so an extension can load its own <c>*.cfg</c>.</summary>
    string ConfigDir { get; }

    /// <summary>Write to the diagnostic log (not the game window).</summary>
    void Log(string message);

    /// <summary>Read a persistent user variable (a <c>#var</c> value, resolved as
    /// <c>$name</c>), or null when the name isn't a user variable. Distinct from
    /// <see cref="Globals"/>, which holds only live game-state + <c>#tvar</c> session
    /// globals — Genie 4 plugin settings like <c>$CircleCalc.Guild</c> live in the
    /// persistent store. Default returns null for hosts that don't expose it.</summary>
    string? GetUserVar(string name) => null;

    /// <summary>Read a persisted <c>#config</c> / settings.cfg value by key (the same
    /// keys <c>#config list</c> shows, e.g. <c>experiencedensity</c>), or null when the
    /// host has no config or the key is unknown. Lets an extension respond to a
    /// <c>#config</c> setting at render time without depending on <c>GenieConfig</c>
    /// directly. Default returns null for hosts that don't expose config.</summary>
    string? GetConfig(string key) => null;

    /// <summary>App-level shared data root (the Genie5 folder itself — the per-user
    /// data dir or the portable dir), for state shared across characters and
    /// profiles: the Genie-4-compatible <c>InventoryView.xml</c> catalog lives here.
    /// Distinct from <see cref="ConfigDir"/>, which is per-profile when profiles are
    /// in effect. Default falls back to <see cref="ConfigDir"/>.</summary>
    string DataRoot => ConfigDir;

    /// <summary>Re-emit a line through the parse pipeline (the <c>#parse</c> seam),
    /// so scripts can <c>waitfor</c> an extension-generated marker. Default no-op
    /// for hosts without a pipeline.</summary>
    void InjectParsedLine(string line) { }

    /// <summary>Echo with explicit control over the two legs plain <see cref="Echo"/>
    /// couples: <paramref name="display"/> shows the line in the game window;
    /// <paramref name="parse"/> feeds it to script actions/triggers (through the same
    /// re-entrancy-guarded <c>#parse</c> seam <see cref="Echo"/> uses, so an
    /// extension's own output can't loop back into it). Lets a plugin honour Genie 4
    /// display/parse toggles (e.g. <c>$CircleCalc.Echo</c> / <c>$CircleCalc.Parse</c>)
    /// independently. Default routes to <see cref="Echo"/> (both legs) so hosts that
    /// don't distinguish them keep current behaviour.</summary>
    void EchoRouted(string text, bool display, bool parse) => Echo(text);

    /// <summary>Run a <c>#command</c> (e.g. <c>#browser &lt;url&gt;</c>) through the
    /// host's command engine — <see cref="SendCommand"/> is game-bound and would leak
    /// a hash command to the server. Default no-op.</summary>
    void RunHashCommand(string command) { }
}
