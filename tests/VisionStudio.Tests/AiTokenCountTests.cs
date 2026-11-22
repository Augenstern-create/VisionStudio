using VisionStudio.Ai.Abstractions;

namespace VisionStudio.Tests;

public sealed class AiTokenCountTests
{
    [Theory]
    [InlineData("1M", 1048576)]
    [InlineData("1m", 1048576)]
    [InlineData("128K", 131072)]
    [InlineData("128k", 131072)]
    [InlineData("64K", 65536)]
    [InlineData("8K", 8192)]
    [InlineData("1.5M", 1572864)]
    [InlineData("1 M", 1048576)]
    [InlineData(" 128 k ", 131072)]
    [InlineData("4096", 4096)]
    [InlineData("1,048,576", 1048576)]
    [InlineData("1_048_576", 1048576)]
    public void Parse_AcceptsHumanTokenBudgets(string text, int expected)
    {
        Assert.True(AiTokenCount.TryParse(text, out int tokens), text);
        Assert.Equal(expected, tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("K")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1X")]
    [InlineData("999999999999G")]
    public void Parse_RejectsInvalidInput(string text)
    {
        Assert.False(AiTokenCount.TryParse(text, out _), text);
    }

    [Theory]
    [InlineData(131072, "128K")]
    [InlineData(1048576, "1M")]
    [InlineData(65536, "64K")]
    [InlineData(64000, "64000")]
    [InlineData(4096, "4K")]
    public void Format_ShowsTheShortestExactForm(int tokens, string expected)
    {
        Assert.Equal(expected, AiTokenCount.Format(tokens));
    }

    [Fact]
    public void Parse_RoundTripsFormattedValues()
    {
        foreach (int value in new[] { 4096, 8192, 65536, 131072, 1048576 })
        {
            Assert.True(AiTokenCount.TryParse(AiTokenCount.Format(value), out int parsed));
            Assert.Equal(value, parsed);
        }
    }
}
