using System.Text;
using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

public static class GoapContextSerializer
{
    public static string Serialize(IWorldState worldState, GoapPlanResult planResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GOAP Analysis:");

        if (planResult.RecommendedPlan is { Count: > 0 } recommended)
        {
            var actions = string.Join(" → ", recommended.Select(a => a.Name));
            var totalCost = recommended.Sum(a => a.Cost);
            sb.AppendLine($"  Recommended plan: {actions} (total cost: {totalCost})");
        }
        else if (planResult.DeadEnd)
        {
            sb.AppendLine("  Recommended plan: (none — dead end)");
        }
        else
        {
            sb.AppendLine("  Recommended plan: (goal already satisfied)");
        }

        if (planResult.AlternativePlan is { Count: > 0 } alternative)
        {
            var actions = string.Join(" → ", alternative.Select(a => a.Name));
            var totalCost = alternative.Sum(a => a.Cost);
            sb.AppendLine($"  Alternative plan: {actions} (total cost: {totalCost})");
        }

        sb.AppendLine($"  Dead end: {planResult.DeadEnd.ToString().ToLowerInvariant()}");

        var stateEntries = worldState.GetAll()
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  Current world state: {string.Join(", ", stateEntries)}");

        return sb.ToString().TrimEnd();
    }
}
