using Deckwraith.Core.Naming;

namespace Deckwraith.Core.Tests;

public sealed class CanonicalNameTests
{
    [Theory]
    [InlineData("Wraith1", "wraith1")]
    [InlineData("Compiler-Lab", "compiler-lab")]
    [InlineData("9", "9")]
    public void ParseNormalizesPortableNames(string input, string expected)
    {
        Assert.Equal(expected, CanonicalName.Parse(input).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" wraith")]
    [InlineData("wraith ")]
    [InlineData("-wraith")]
    [InlineData("wraith-")]
    [InlineData("wraith_name")]
    [InlineData("wraith/name")]
    [InlineData("wraith.name")]
    [InlineData("séra")]
    [InlineData("CON")]
    public void ParseRejectsNonportableOrReservedNames(string input)
    {
        Assert.Throws<ArgumentException>(() => CanonicalName.Parse(input));
    }

    [Fact]
    public void ParseRejectsNamesOverTheLengthLimit()
    {
        Assert.Throws<ArgumentException>(() => CanonicalName.Parse(new string('a', 64)));
    }

    [Fact]
    public void EqualityUsesTheCanonicalCaseFoldedValue()
    {
        Assert.Equal(CanonicalName.Parse("Vesper"), CanonicalName.Parse("vesper"));
    }
}
