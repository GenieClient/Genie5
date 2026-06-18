namespace Genie.Core.Scripting;

/// <summary>
/// Session-wide DR type-ahead limit, shared by the mapper and the script engine
/// (how many commands may be pipelined before the server rejects with "you may
/// only type ahead N lines"). DR caps this per account: free/basic = 1, premium
/// = 2, premium+LTB = 3.
///
/// Lifecycle:
/// <list type="bullet">
/// <item>On a DirectSGE connect, <see cref="GenieCore"/> seeds it from the
///   account tier reported by the SGE login (1 free / 2 premium).</item>
/// <item>It then self-calibrates to the authoritative value if the server ever
///   reports its cap ("(Sorry,) you may only type ahead N line(s).") — see
///   <c>GenieCore.CalibrateTypeAhead</c>. This corrects a mis-seed in either
///   direction (e.g. premium+LTB up to 3, or a too-high Lich/DevReplay default).</item>
/// </list>
/// The default below is the pre-connect / non-SGE (Lich, DevReplay) fallback;
/// those paths don't know the tier and rely on cap-message calibration.
/// </summary>
public sealed class TypeAheadSession
{
    private int _limit = 3;
    private int _inFlight;

    /// <summary>Raised whenever <see cref="Limit"/> or <see cref="InFlight"/>
    /// changes, so a UI counter can refresh. May fire on any thread — handlers
    /// must marshal to the UI thread themselves.</summary>
    public event Action? Changed;

    /// <summary>Max commands that may be pipelined ahead of the game (see class
    /// summary). Seeded from the account tier and calibrated from the server cap.</summary>
    public int Limit
    {
        get => _limit;
        set { if (value != _limit) { _limit = value; Changed?.Invoke(); } }
    }

    /// <summary>Commands sent to the game that the server hasn't acknowledged
    /// yet (incremented on send, decremented on each game prompt). Clamped to
    /// ≥ 0. This is the live "type-ahead buffer occupancy" the UI counter shows.</summary>
    public int InFlight
    {
        get => _inFlight;
        private set
        {
            var v = value < 0 ? 0 : value;
            if (v != _inFlight) { _inFlight = v; Changed?.Invoke(); }
        }
    }

    /// <summary>A command was sent to the game — one type-ahead slot consumed.</summary>
    public void NotifySent() => InFlight = _inFlight + 1;

    /// <summary>The game emitted a prompt — the server processed input, so one
    /// slot frees. No-op when already empty (floored at 0).</summary>
    public void NotifyConsumed() => InFlight = _inFlight - 1;

    /// <summary>Clear the in-flight count (e.g. on (re)connect).</summary>
    public void ResetInFlight() => InFlight = 0;
}
