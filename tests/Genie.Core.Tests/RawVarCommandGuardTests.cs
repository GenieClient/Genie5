using Genie.Core;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// A command reaching the game with a surviving <c>%name</c>/<c>$name</c> token
/// is an unexpanded (undefined) variable being sent literally — the sibling of
/// the phantom-window echo guard. This is what makes a script's
/// <c>put go %offtransport</c> (with <c>%offtransport</c> unset) go out as the
/// literal text <c>go %offtransport</c>, which DR answers with "What were you
/// referring to?". <see cref="GenieCore.ContainsUnexpandedVar"/> is the predicate
/// behind the <c>#config warnrawvars</c> diagnostic; this locks it.
/// </summary>
public class RawVarCommandGuardTests
{
    [Theory]
    [InlineData("go %offtransport", true, "%offtransport")]   // the ferry bug
    [InlineData("get $righthandnoun", true, "$righthandnoun")] // undefined global
    [InlineData("put %spell.Prep", true, "%spell.Prep")]       // dotted member
    [InlineData("cast $target_one", true, "$target_one")]      // underscore name
    [InlineData("go dock", false, "")]                          // clean command
    [InlineData("cast 101", false, "")]                         // digits only
    [InlineData("say I have $5 left", false, "")]               // '$' before a digit = not a var ref
    [InlineData("give 50% effort", false, "")]                  // bare '%' not followed by a name
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void Flags_only_unexpanded_variable_tokens(string? command, bool expected, string expectedToken)
    {
        var hit = GenieCore.ContainsUnexpandedVar(command, out var token);
        Assert.Equal(expected, hit);
        Assert.Equal(expectedToken, token);
    }
}
