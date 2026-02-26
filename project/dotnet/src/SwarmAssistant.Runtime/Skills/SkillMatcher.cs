using System.Text.RegularExpressions;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Skills;

public sealed partial class SkillMatcher
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "have", "been", "were", "they", "their",
        "would", "could", "should", "what", "when", "where", "which", "while",
        "about", "after", "before", "between", "under", "other", "more", "some",
        "such", "only", "also", "than", "then", "very", "just", "into", "over"
    };

    private readonly IReadOnlyList<SkillDefinition> _skills;

    public SkillMatcher(IReadOnlyList<SkillDefinition> skills)
    {
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
    }

    public IReadOnlyList<MatchedSkill> Match(
        string taskTitle,
        string taskDescription,
        SwarmRole role,
        int maxResults = 5,
        int budgetChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(taskTitle))
        {
            return Array.Empty<MatchedSkill>();
        }

        var taskKeywords = TokenizeAndFilter($"{taskTitle} {taskDescription ?? string.Empty}");
        if (taskKeywords.Count == 0)
        {
            return Array.Empty<MatchedSkill>();
        }

        var matches = new List<MatchedSkill>();

        foreach (var skill in _skills)
        {
            if (skill.Tags.Count == 0 || !skill.Roles.Contains(role))
            {
                continue;
            }

            var matchedTags = skill.Tags
                .Where(tag => taskKeywords.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (matchedTags.Count > 0)
            {
                var score = (double)matchedTags.Count / skill.Tags.Count;
                matches.Add(new MatchedSkill(skill, score, matchedTags));
            }
        }

        var sortedMatches = matches
            .OrderByDescending(m => m.RelevanceScore)
            .ThenBy(m => m.Definition.Name, StringComparer.Ordinal)
            .ToList();

        return ApplyBudgetConstraint(sortedMatches, maxResults, budgetChars);
    }

    private static HashSet<string> TokenizeAndFilter(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = WordRegex().Matches(text.ToLowerInvariant());

        foreach (Match match in matches)
        {
            var word = match.Value;
            if (!StopWords.Contains(word))
            {
                tokens.Add(word);
            }
        }

        return tokens;
    }

    private static IReadOnlyList<MatchedSkill> ApplyBudgetConstraint(
        List<MatchedSkill> matches,
        int maxResults,
        int budgetChars)
    {
        var result = new List<MatchedSkill>();
        var totalChars = 0;

        foreach (var match in matches.Take(maxResults))
        {
            var bodyLength = match.Definition.Body?.Length ?? 0;
            if (totalChars + bodyLength <= budgetChars)
            {
                result.Add(match);
                totalChars += bodyLength;
            }
            else
            {
                continue;
            }
        }

        return result;
    }

    [GeneratedRegex(@"[a-z]{3,}")]
    private static partial Regex WordRegex();
}
