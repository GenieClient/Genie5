using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

public class ZoneConnectionChangeTests
{
    [Fact]
    public void EditingTheListedFields_RaisesPropertyChanged_OnlyWhenTheValueChanges()
    {
        var c = new ZoneConnection();
        var seen = new List<string?>();
        c.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        c.FromZone = "Map150_Fang_Cove";
        c.ToZone   = "Map1_Crossing";
        c.Verb     = "go exit portal";
        c.Verb     = "go exit portal";     // no change, no event

        Assert.Equal(new[] { "FromZone", "ToZone", "Verb" }, seen);
    }
}
