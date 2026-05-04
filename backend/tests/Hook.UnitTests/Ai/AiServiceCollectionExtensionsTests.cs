using Hook.Features.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class AiServiceCollectionExtensionsTests
{
    [Fact]
    public void AddConversationAi_ShouldRegisterOllamaImpl_EvenWhenDevAiEnabledTrue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dev:Ai:Enabled"] = "true",
                ["Ollama:BaseUrl"] = "http://localhost:11434"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConversationAi(config);

        using var sp = services.BuildServiceProvider();
        var ai = sp.GetRequiredService<IConversationAi>();

        ai.ShouldBeOfType<OllamaConversationAi>();
    }

    [Fact]
    public void AddConversationAi_ShouldRegisterOllamaImpl_WhenDevAiEnabledAbsent()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://localhost:11434"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConversationAi(config);

        using var sp = services.BuildServiceProvider();
        var ai = sp.GetRequiredService<IConversationAi>();

        ai.ShouldBeOfType<OllamaConversationAi>();
    }
}
