using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

internal static class AgentStatusResolver
{
    internal static string ResolveAgentStatus(BudgetEnvelope? budget, int consecutiveFailures)
    {
        if (budget?.IsExhausted == true)
        {
            return "exhausted";
        }

        if (budget?.IsLowBudget == true)
        {
            return "low-budget";
        }

        if (consecutiveFailures > 0)
        {
            return "unhealthy";
        }

        return "active";
    }
}
