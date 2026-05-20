using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Refresh;
using Hook.Features.Whatsapp.Phone;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Wolverine;
using ProviderEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.UnitTests.ProviderAvailability;

public class ProviderRefreshCheckHandlerTests
{
    private readonly Mock<IProviderAvailabilityRepository> _repo = new();
    private readonly Mock<IConversationAi> _ai = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<SendWhatsAppTextRequested> _sent = [];

    public ProviderRefreshCheckHandlerTests()
    {
        _bus.Setup(b => b.PublishAsync(It.IsAny<SendWhatsAppTextRequested>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((msg, _) => _sent.Add((SendWhatsAppTextRequested)msg))
            .Returns(ValueTask.CompletedTask);
    }

    private ProviderRefreshCheckHandler Build() => new(
        _repo.Object, _ai.Object, _bus.Object,
        NullLogger<ProviderRefreshCheckHandler>.Instance);

    private static ProviderEntity SeedProvider() => ProviderEntity.Register(
        "+2203331234", ["plumbing"],
        new Location(13.45, -16.6), "Banjul",
        true, TimeSpan.FromHours(24), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Handle_ProviderMissing_NoSend()
    {
        _repo.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ProviderEntity?)null);

        await Build().Handle(new ProviderRefreshCheck("+2203331234", DateTimeOffset.UtcNow), CancellationToken.None);

        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_LastActiveAfterEventCutoff_NoSend()
    {
        var provider = SeedProvider();
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        provider.Heartbeat(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
        _repo.Setup(r => r.GetAsync(provider.Phone, It.IsAny<CancellationToken>())).ReturnsAsync(provider);

        await Build().Handle(new ProviderRefreshCheck(provider.Phone, cutoff), CancellationToken.None);

        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AiReturnsNull_NoSend()
    {
        var provider = SeedProvider();
        _repo.Setup(r => r.GetAsync(provider.Phone, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        await Build().Handle(new ProviderRefreshCheck(provider.Phone, DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        _sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_AiReturnsText_PublishesSendToProviderPhone()
    {
        var provider = SeedProvider();
        _repo.Setup(r => r.GetAsync(provider.Phone, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Are you still available?");

        await Build().Handle(new ProviderRefreshCheck(provider.Phone, DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        _sent.ShouldHaveSingleItem();
        _sent[0].To.Value.ShouldBe(provider.Phone);
        _sent[0].Text.ShouldBe("Are you still available?");
    }

    [Fact]
    public async Task Handle_FactsContainsCommaJoinedServicesAndInstruction()
    {
        var provider = SeedProvider();
        ReplyContext? captured = null;
        _repo.Setup(r => r.GetAsync(provider.Phone, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .Callback<ReplyContext, CancellationToken>((c, _) => captured = c)
            .ReturnsAsync("reply");

        await Build().Handle(new ProviderRefreshCheck(provider.Phone, DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Facts["services"].ShouldBe("plumbing");
        captured!.Facts["instruction"].ShouldContain("still available");
    }
}
