using SwarmAssistant.Runtime.Actors;

namespace SwarmAssistant.Runtime.Tests;

public sealed class QualityEvaluatorTests
{
    [Fact]
    public void GetAlternativeAdapter_UsesConfiguredOrder()
    {
        var configured = new[] { "kimi", "kilo" };

        Assert.Equal("kilo", QualityEvaluator.GetAlternativeAdapter("kimi", configured));
        Assert.Equal("kimi", QualityEvaluator.GetAlternativeAdapter("kilo", configured));
        Assert.Equal("kimi", QualityEvaluator.GetAlternativeAdapter("cline", configured));
    }

    [Fact]
    public void GetAlternativeAdapter_IgnoresEmptyAndDuplicateConfiguredValues()
    {
        var configured = new[] { "kimi", "", "KIMI", "kilo" };
        Assert.Equal("kilo", QualityEvaluator.GetAlternativeAdapter("kimi", configured));
    }

    [Fact]
    public void GetAdapterReliabilityScore_KiloMatchesKimiBaseline()
    {
        Assert.Equal(0.80, QualityEvaluator.GetAdapterReliabilityScore("kilo"));
    }
}
