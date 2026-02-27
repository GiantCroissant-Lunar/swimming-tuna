using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Skills;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Execution;

internal static class RolePromptFactory
{
    public static string BuildPrompt(ExecuteRoleTask command)
    {
        return BuildPrompt(command, strategyAdvice: null, codeContext: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice)
    {
        return BuildPrompt(command, strategyAdvice, codeContext: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice, CodeIndexResult? codeContext)
    {
        return BuildPrompt(command, strategyAdvice, codeContext, projectContext: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice, CodeIndexResult? codeContext, string? projectContext)
    {
        return BuildPrompt(command, strategyAdvice, codeContext, projectContext, matchedSkills: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice, CodeIndexResult? codeContext, string? projectContext, IReadOnlyList<MatchedSkill>? matchedSkills)
    {
        return BuildPrompt(command, strategyAdvice, codeContext, projectContext, matchedSkills, langfuseContext: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice, CodeIndexResult? codeContext, string? projectContext, IReadOnlyList<MatchedSkill>? matchedSkills, string? langfuseContext)
    {
        return BuildPrompt(command, strategyAdvice, codeContext, projectContext, matchedSkills, langfuseContext, siblingContext: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice, CodeIndexResult? codeContext, string? projectContext, IReadOnlyList<MatchedSkill>? matchedSkills, string? langfuseContext, string? siblingContext)
    {
        var basePrompt = command.Role switch
        {
            SwarmRole.Planner => string.Join(
                Environment.NewLine,
                "You are the planner agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                "Return a concise implementation plan with risks and validation steps."),
            SwarmRole.Builder => string.Join(
                Environment.NewLine,
                "You are a builder agent. Given the task and implementation plan below, produce concrete",
                "implementation. Write only the code changes needed. Do not explain — just implement.",
                string.Empty,
                "## Task",
                $"Title: {command.Title}",
                $"Description: {command.Description}",
                string.Empty,
                "## Implementation Plan",
                command.PlanningOutput ?? "No plan provided. Infer the necessary changes from the task description.",
                string.Empty,
                "## Code Quality",
                "Pre-commit hooks enforce: no trailing whitespace, files must end with a newline,",
                "no merge conflict markers. Ensure all produced code satisfies these constraints.",
                string.Empty,
                "Produce the minimal code changes to complete this task. Include file paths for each change."),
            SwarmRole.Reviewer => string.Join(
                Environment.NewLine,
                "You are the reviewer agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Planner output: {command.PlanningOutput ?? "(none)"}",
                $"Builder output: {command.BuildOutput ?? "(none)"}",
                string.Empty,
                "## Review Checklist",
                "1. Find defects, risks, and missing tests. Keep it specific.",
                "2. Cross-validate specs against implementation:",
                "   - If OpenAPI/JSON schemas were modified, verify field types, nullability, and required",
                "     arrays match the DTO/model signatures in code.",
                "   - If query parameters or enum values appear in both spec and code, confirm descriptions",
                "     and allowed values are consistent.",
                "3. If generated model files exist (Models.g.cs, models.g.ts), note whether the OpenAPI",
                "   schema changes would make them stale and flag if regeneration is needed.",
                "4. Check that status/state fields carry semantic meaning rather than duplicating raw",
                "   enum values from other fields."),
            SwarmRole.Orchestrator => command.OrchestratorPrompt
                ?? "You are the orchestrator agent. Decide the next action. Respond with ACTION: <action> and REASON: <reason>.",
            SwarmRole.Researcher => string.Join(
                Environment.NewLine,
                "You are the researcher agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                "Collect relevant facts and references that de-risk implementation decisions."),
            SwarmRole.Debugger => string.Join(
                Environment.NewLine,
                "You are the debugger agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                $"Builder output: {command.BuildOutput ?? "(none)"}",
                "Identify likely failure points, root causes, and minimal fixes."),
            SwarmRole.Tester => string.Join(
                Environment.NewLine,
                "You are the tester agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                $"Planner output: {command.PlanningOutput ?? "(none)"}",
                $"Builder output: {command.BuildOutput ?? "(none)"}",
                "Propose focused test cases that validate functionality and regressions."),
            SwarmRole.Decomposer => string.Join(
                Environment.NewLine,
                "You are the decomposer agent in a swarm runtime.",
                $"Run title: {command.Title}",
                string.Empty,
                "## Document",
                command.Description,
                string.Empty,
                "## Output Format",
                "Return ONLY a valid JSON array of task definitions. No explanations, no markdown code blocks.",
                string.Empty,
                "The JSON array must have this exact structure:",
                "[",
                "  {",
                "    \"title\": \"Task title string\",",
                "    \"description\": \"Detailed description of what needs to be done\",",
                "    \"priority\": 1",
                "  }",
                "]",
                string.Empty,
                "Requirements:",
                "- Output ONLY the raw JSON array, nothing else",
                "- Each task must have a unique, descriptive title",
                "- Descriptions should be actionable and specific",
                "- Priority values should be unique integers starting from 1",
                "- Return a flat array, not nested structures"),
            _ => $"Unsupported role {command.Role}"
        };

        var contextParts = new List<string>();

        // Project context (2nd layer)
        if (!string.IsNullOrWhiteSpace(projectContext) &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            contextParts.Add(BuildProjectContext(projectContext));
        }

        // Historical context (3rd layer)
        if (strategyAdvice is not null &&
            strategyAdvice.SimilarTaskCount > 0 &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            contextParts.Add(BuildHistoricalContext(strategyAdvice));
        }

        // Skill context (5th layer)
        if (matchedSkills is { Count: > 0 } &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            contextParts.Add(BuildSkillContext(matchedSkills));
        }

        // Code context (4th layer)
        if (codeContext is not null &&
            codeContext.HasResults &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            contextParts.Add(BuildCodeContext(codeContext));
        }

        // Langfuse context (6th layer)
        if (!string.IsNullOrWhiteSpace(langfuseContext) &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            contextParts.Add("\n### Historical Learning (Langfuse)\n" + langfuseContext);
        }

        // Sibling context (7th layer)
        if (!string.IsNullOrWhiteSpace(siblingContext) &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            const int maxSiblingContextChars = 8_000;
            var truncated = siblingContext.Length > maxSiblingContextChars
                ? siblingContext[..maxSiblingContextChars] + "\n... (truncated)"
                : siblingContext;
            contextParts.Add("\n### Sibling Task Context\n" + truncated);
        }

        if (contextParts.Count == 0)
        {
            return basePrompt;
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            new[] { basePrompt }.Concat(contextParts));
    }

    /// <summary>
    /// Builds a historical context section from strategy advice.
    /// </summary>
    private static string BuildHistoricalContext(StrategyAdvice advice)
    {
        var lines = new List<string>
        {
            "--- Historical Insights ---",
            $"Based on {advice.SimilarTaskCount} similar past tasks:",
            $"  Success rate: {advice.SimilarTaskSuccessRate:P0}",
        };

        if (advice.AverageRetryCount > 0)
        {
            lines.Add($"  Average retries: {advice.AverageRetryCount:F1}");
        }

        if (advice.ReviewRejectionRate > 0.1)
        {
            lines.Add($"  Review rejection rate: {advice.ReviewRejectionRate:P0}");
        }

        // Add insights
        foreach (var insight in advice.Insights)
        {
            lines.Add($"  • {insight}");
        }

        // Add adapter recommendations if available
        if (advice.AdapterSuccessRates is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Adapter performance for similar tasks:");
            foreach (var (adapter, rate) in advice.AdapterSuccessRates.OrderByDescending(kv => kv.Value))
            {
                lines.Add($"  {adapter}: {rate:P0} success rate");
            }
        }

        // Add common failure patterns if available
        if (advice.CommonFailurePatterns is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Common failure patterns to avoid:");
            foreach (var pattern in advice.CommonFailurePatterns)
            {
                lines.Add($"  • {pattern}");
            }
        }

        lines.Add("--- End Historical Insights ---");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Builds a project context section from AGENTS.md or similar project-level guidance.
    /// This is the 2nd context layer: static project knowledge injected before dynamic insights.
    /// </summary>
    private static string BuildProjectContext(string projectContext)
    {
        return string.Join(
            Environment.NewLine,
            "## Project Context",
            projectContext,
            "## End Project Context");
    }

    /// <summary>
    /// Builds a skill context section from matched skills.
    /// This is the 5th context layer: agent skills injected between historical and code context.
    /// Total budget is capped at 4,000 characters to avoid overwhelming the LLM context window.
    /// </summary>
    private static string BuildSkillContext(IReadOnlyList<MatchedSkill> skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return string.Empty;
        }

        const int maxTotalChars = 4_000;
        var lines = new List<string>
        {
            "--- Agent Skills ---"
        };

        var totalChars = 0;
        foreach (var skill in skills)
        {
            var skillName = skill.Definition.Name;
            var skillBody = skill.Definition.Body;

            var header = $"### {skillName}\n";
            var availableSpace = maxTotalChars - totalChars - header.Length - 2; // 2 for newlines

            if (availableSpace <= 0)
            {
                break;
            }

            var bodyToAdd = skillBody.Length <= availableSpace
                ? skillBody
                : skillBody[..availableSpace];

            lines.Add(header + bodyToAdd);
            lines.Add(string.Empty);

            totalChars += header.Length + bodyToAdd.Length + 2;

            if (totalChars >= maxTotalChars)
            {
                break;
            }
        }

        lines.Add("--- End Agent Skills ---");
        lines.Add("Apply these skills to your review/implementation.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Builds a code context section from code index results.
    /// This is the 4th context layer: relevant codebase structure for the task.
    /// Total budget is capped at 40,000 characters to avoid overwhelming the LLM context window.
    /// </summary>
    private static string BuildCodeContext(CodeIndexResult result)
    {
        const int maxTotalChars = 40_000;
        const int maxPerChunkChars = 2_000;

        var lines = new List<string>
        {
            "--- Relevant Code Context ---",
            $"Query: {result.Query}",
            $"Found {result.Chunks.Count} relevant code units:",
            string.Empty
        };

        var totalChars = 0;
        foreach (var chunk in result.Chunks)
        {
            var content = chunk.Content.Length > maxPerChunkChars
                ? chunk.Content[..maxPerChunkChars] + "\n... (truncated)"
                : chunk.Content;

            if (totalChars + content.Length > maxTotalChars)
                break;

            var langTag = chunk.Language switch
            {
                "csharp" => "csharp",
                "javascript" => "javascript",
                "typescript" => "typescript",
                "python" => "python",
                _ => ""
            };

            lines.Add($"### {chunk.FullyQualifiedName}");
            lines.Add($"File: {chunk.FilePath} (lines {chunk.StartLine}-{chunk.EndLine})");
            lines.Add($"Type: {chunk.NodeType} | Language: {chunk.Language} | Relevance: {chunk.SimilarityScore:P0}");
            lines.Add($"```{langTag}");
            lines.Add(content);
            lines.Add("```");
            lines.Add(string.Empty);

            totalChars += content.Length;
        }

        lines.Add("--- End Code Context ---");
        lines.Add("Use these code patterns as reference when planning, building, or reviewing.");

        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildOrchestratorPrompt(
        string taskId,
        string title,
        string description,
        string goapContext,
        IReadOnlyDictionary<string, string>? blackboardEntries,
        IReadOnlyDictionary<string, string>? globalBlackboardEntries = null)
    {
        var lines = new List<string>
        {
            $"You are the orchestrator agent for task '{title}'.",
            $"Task ID: {taskId}",
            $"Task description: {description}",
            string.Empty,
            goapContext,
            string.Empty,
        };

        if (blackboardEntries is { Count: > 0 })
        {
            lines.Add("Task history:");
            foreach (var (key, value) in blackboardEntries)
            {
                lines.Add($"  {key}: {value}");
            }

            lines.Add(string.Empty);
        }

        // Include global blackboard context for stigmergy (cross-task coordination)
        if (globalBlackboardEntries is { Count: > 0 })
        {
            lines.Add("Swarm intelligence signals:");
            foreach (var (key, value) in globalBlackboardEntries)
            {
                lines.Add($"  {key}: {value}");
            }

            lines.Add(string.Empty);
        }

        lines.Add("What should happen next? Choose ONE action from the GOAP plan and explain why.");
        lines.Add("Respond in this format:");
        lines.Add("ACTION: <action name>");
        lines.Add("REASON: <brief explanation>");

        return string.Join(Environment.NewLine, lines);
    }
}
