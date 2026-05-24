using Hook.Features.Ai;
using Wolverine.Attributes;

namespace Hook.UnitTests.Architecture;

public class AiHandlerArchitectureTests
{
    // A handler that depends on IConversationAi MUST be [NonTransactional] — otherwise
    // AutoApplyTransactions pins an Npgsql connection across the 60–150s Ollama window.
    [Fact]
    public void EveryHandlerDependingOnIConversationAi_IsNonTransactional()
    {
        var offenders = typeof(IConversationAi).Assembly.GetTypes()
            .Where(t => t.IsClass
                && !t.IsAbstract
                && t.Name.EndsWith("Handler", StringComparison.Ordinal)
                && t.GetConstructors().Any(c =>
                    c.GetParameters().Any(p => p.ParameterType == typeof(IConversationAi))))
            .Where(t => t.GetMethods()
                .Where(m => m.Name == "Handle" || m.Name == "HandleAsync")
                .All(m => !m.GetCustomAttributes(typeof(NonTransactionalAttribute), inherit: false).Any()))
            .Select(t => t.FullName!)
            .ToList();

        Assert.Empty(offenders);
    }
}
