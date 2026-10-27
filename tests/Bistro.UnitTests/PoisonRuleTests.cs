using Bistro.Shared;
using Xunit;

namespace Bistro.UnitTests;

public class PoisonRuleTests
{
    [Theory]
    [InlineData("POISON")]
    [InlineData("RSV-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void Substring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(PoisonRule.IsPoison(reference));

    [Theory]
    [InlineData("RSV-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    [InlineData("")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(PoisonRule.IsPoison(reference));
}
