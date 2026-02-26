namespace SwarmAssistant.Runtime.Skills;

public sealed record MatchedSkill
{
    private readonly SkillDefinition _definition = null!;
    private readonly double _relevanceScore;
    private readonly IReadOnlyList<string> _matchedTags = Array.Empty<string>();

    public SkillDefinition Definition
    {
        get => _definition;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _definition = value;
        }
    }

    public double RelevanceScore
    {
        get => _relevanceScore;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1.0);
            _relevanceScore = value;
        }
    }

    public IReadOnlyList<string> MatchedTags
    {
        get => _matchedTags;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _matchedTags = value;
        }
    }

    public MatchedSkill(
        SkillDefinition Definition,
        double RelevanceScore,
        IReadOnlyList<string> MatchedTags)
    {
        this.Definition = Definition;
        this.RelevanceScore = RelevanceScore;
        this.MatchedTags = MatchedTags;
    }
}
