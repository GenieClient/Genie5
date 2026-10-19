using System.Reactive.Linq;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

public class LinkRoomsViewModelTests
{
    [Theory]
    [InlineData("west", "east")]
    [InlineData("NE", "southwest")]
    [InlineData("go oak door", "")]
    [InlineData("", "")]
    public void SuggestReturn_IsTheOppositeCompassOrBlank(string verb, string expected)
        => Assert.Equal(expected, LinkRoomsViewModel.SuggestReturn(verb));

    [Fact]
    public void ReturnVerb_FollowsTheVerbUntilTheAuthorEditsIt()
    {
        var vm = new LinkRoomsViewModel("a", "b") { Verb = "north" };
        Assert.Equal("south", vm.ReturnVerb);

        vm.ReturnVerb = "go back door";
        vm.Verb = "east";
        Assert.Equal("go back door", vm.ReturnVerb);
    }

    [Fact]
    public async Task Ok_RequiresAVerb()
    {
        var vm = new LinkRoomsViewModel("a", "b");
        Assert.False(await vm.OkCommand.CanExecute.FirstAsync());
        vm.Verb = "west";
        Assert.True(await vm.OkCommand.CanExecute.FirstAsync());
    }
}
