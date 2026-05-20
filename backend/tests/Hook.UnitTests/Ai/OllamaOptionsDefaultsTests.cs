using Hook.Features.Ai;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class OllamaOptionsDefaultsTests
{
    [Fact]
    public void Defaults_KeepAlive_MatchesIntent()
    {
        // Memory-aware default for single-tenant dev/single-box deploys — must stay
        // in sync with appsettings.json so deploys reading the default get the same.
        new OllamaOptions().KeepAlive.ShouldBe("30m");
    }

    [Fact]
    public void Defaults_TimeoutSeconds_120()
    {
        new OllamaOptions().TimeoutSeconds.ShouldBe(120);
    }
}
