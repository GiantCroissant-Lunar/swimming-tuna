namespace SwarmAssistant.Runtime.Skills;

public sealed record MatchedSkill
{
    public SkillDefinition Definition { get; init; }
    public double RelevanceScore { get; init; }
    public IReadOnlyList<string> MatchedTags { get; init; }

    public MatchedSkill(
        SkillDefinition Definition,
        double RelevanceScore,
        IReadOnlyList<string> MatchedTags)
    {
        ArgumentNullException.ThrowIfNull(Definition);
        ArgumentNullException.ThrowIfNull(MatchedTags);
        ArgumentOutOfRangeException.ThrowIfNegative(RelevanceScore);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RelevanceScore, 1.0);

        this.Definition = Definition;
        this.RelevanceScore = RelevanceScore;
        this.MatchedTags = MatchedTags;
    }
}
