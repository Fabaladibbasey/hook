using Hook.Features.Ai;
using Hook.TestHelpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Wolverine;

namespace Hook.IntegrationTests;

[Collection("Pipeline-Migration")]
public sealed class StaticCodegenSmokeTests : PipelineTestBase
{
    public StaticCodegenSmokeTests(DevPipelineFixture fx) : base(fx) { }

    // Production runs Wolverine with default static codegen; the parallel-shard
    // fixture sets DynamicCodegen=true for speed. This test rebuilds the host with
    // DynamicCodegen=false to keep static-codegen from silently breaking on a
    // new handler signature.
    [Fact]
    public async Task Host_BuildsWithStaticCodegen_AndExposesMessageBus()
    {
        var shardConn = _fx.Factory.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString("HookDb")!;

        await using var staticFactory = new WebApplicationFactory<global::Hook.Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.UseSetting("ConnectionStrings:HookDb", shardConn);
                b.UseSetting("Whatsapp:VerifyToken", "v");
                b.UseSetting("Whatsapp:AppSecret", "s");
                b.UseSetting("Whatsapp:PhoneNumberId", "PN-1");
                b.UseSetting("Whatsapp:AccessToken", "token");
                b.UseSetting("GoogleGeocoding:ApiKey", "k");
                b.UseSetting("Dev:Whatsapp:Enabled", "true");
                b.UseSetting("Dev:Geocoding:Enabled", "true");
                b.UseSetting("Dev:Providers:Enabled", "false");
                b.UseSetting("Retention:Enabled", "false");
                b.UseSetting("Wolverine:DefaultExecutionTimeoutSeconds", "10");
                b.UseSetting("Wolverine:DynamicCodegen", "false");
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<IConversationAi>();
                    s.AddSingleton<IConversationAi, FakeConversationAi>();
                });
            });

        // Force host build by resolving any registered service.
        var bus = staticFactory.Services.GetRequiredService<IMessageBus>();
        bus.ShouldNotBeNull();

        // Issue a trivial HTTP call to confirm the pipeline answers.
        using var client = staticFactory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        resp.IsSuccessStatusCode.ShouldBeTrue();
    }
}
