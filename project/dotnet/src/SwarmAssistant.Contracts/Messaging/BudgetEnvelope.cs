namespace SwarmAssistant.Contracts.Messaging;

public enum BudgetType { Unlimited, TokenLimited, RateLimited, PayPerUse }

public sealed record BudgetEnvelope
{
    public BudgetType Type { get; init; } = BudgetType.Unlimited;
    public long TotalTokens { get; init; }
    public long UsedTokens { get; init; }
    public double WarningThreshold { get; init; } = 0.8;
    public double HardLimit { get; init; } = 1.0;

    public double UsedFraction => TotalTokens > 0
        ? Math.Clamp((double)UsedTokens / TotalTokens, 0.0, 1.0)
        : 0.0;

    public double RemainingFraction => TotalTokens > 0
        ? Math.Max(0.0, 1.0 - ((double)UsedTokens / TotalTokens))
        : 1.0;

    public bool IsLowBudget => TotalTokens > 0 &&
        UsedFraction >= WarningThreshold &&
        UsedFraction < HardLimit;

    public bool IsExhausted => Type != BudgetType.Unlimited &&
        (TotalTokens <= 0 || UsedFraction >= HardLimit);
}
