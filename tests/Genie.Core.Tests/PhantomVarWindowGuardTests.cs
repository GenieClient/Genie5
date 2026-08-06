using Genie.Core;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// A directed echo whose resolved <c>&gt;window</c> target still contains '$'
/// is an unresolved script variable — Genie substitutes every DEFINED
/// <c>$var</c>, so a surviving '$' means the name was undefined or mistyped
/// (the classic <c>&gt;$Log</c>-for-<c>&gt;Log</c> typo). Genie 4 dropped such
/// echoes silently; Genie 5 would otherwise manufacture a phantom "$Log" dock
/// window. <see cref="GenieCore.IsUnresolvedVarWindow"/> is the predicate that
/// routes those to Main + a one-time warning instead (see the phantom-window
/// guard note in <c>GenieCore.RaiseEchoToWindow</c>). This locks it so the
/// legitimate custom-window feature (real names like ExpMods/Data) is untouched.
/// </summary>
public class PhantomVarWindowGuardTests
{
    [Theory]
    [InlineData("$Log", true)]        // the canonical typo (undefined $Log)
    [InlineData("$log", true)]        // case variant
    [InlineData("$data", true)]       // any unresolved $var target
    [InlineData("Room$id", true)]     // '$' surviving mid-name is still unresolved
    [InlineData("Log", false)]        // the intended stream window
    [InlineData("ExpMods", false)]    // a real declared custom window (uber.cmd)
    [InlineData("Data", false)]       // a real auto-created custom window (skilldata.cmd)
    [InlineData("CombatTest", false)] // lowranks.cmd
    [InlineData("main", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Flags_only_unresolved_variable_targets(string? window, bool expected)
        => Assert.Equal(expected, GenieCore.IsUnresolvedVarWindow(window));
}
