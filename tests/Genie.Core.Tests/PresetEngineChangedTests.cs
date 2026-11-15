using Genie.Core.Presets;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>#406 review — PresetEngine reports changes so a typed #preset can
/// repaint, as Genie 4's EventPresetChanged does.</summary>
public class PresetEngineChangedTests
{
    [Fact]
    public void Apply_and_reset_raise_Changed()
    {
        var engine = new PresetEngine();
        int raised = 0;
        engine.Changed += () => raised++;

        engine.Apply(new PresetRule { Id = "health", ForegroundColor = "Red" });
        Assert.Equal(1, raised);

        engine.ResetToDefaults();
        Assert.Equal(2, raised);
    }
}
