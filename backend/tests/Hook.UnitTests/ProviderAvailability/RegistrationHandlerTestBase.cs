using Hook.Features.Geocoding.Models;
using Hook.Features.ProviderAvailability;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Shared.Pipeline.PostCommitSends;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Wolverine;
using AvailabilityEntity = Hook.Features.ProviderAvailability.AvailabilityAggregate.ProviderAvailability;

namespace Hook.UnitTests.ProviderAvailability;

public abstract class RegistrationHandlerTestBase
{
    protected const string TestPhone = "+220300001";

    protected readonly Mock<IRegistrationDraftRepository> _draftsMock = new();
    protected readonly Mock<IProviderAvailabilityRepository> _availabilityMock = new();
    protected readonly Mock<IMessageBus> _busMock = new();
    protected readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    protected readonly ProviderAvailabilityOptions _options = new();
    protected readonly List<SendWhatsAppTextCommand> _sent = [];
    protected readonly List<RegistrationDraft> _upserted = [];

    protected RegistrationHandlerTestBase()
    {
        _busMock.Setup(x => x.PublishAsync(It.IsAny<SendWhatsAppTextCommand>(), It.IsAny<DeliveryOptions>()))
            .Callback<object, DeliveryOptions>((m, _) => _sent.Add((SendWhatsAppTextCommand)m))
            .Returns(ValueTask.CompletedTask);
        _draftsMock.Setup(x => x.UpsertAsync(It.IsAny<RegistrationDraft>(), It.IsAny<CancellationToken>()))
            .Callback<RegistrationDraft, CancellationToken>((d, _) => _upserted.Add(d))
            .Returns(Task.CompletedTask);
    }

    protected AvailabilityEntity SeedListed(params string[] services)
    {
        var listed = AvailabilityEntity.Register(
            TestPhone, services, new Location(13.45, -16.6), "Banjul",
            shareContact: true, TimeSpan.FromHours(24), _clock.GetUtcNow());
        _availabilityMock.Setup(x => x.GetAsync(TestPhone, It.IsAny<CancellationToken>())).ReturnsAsync(listed);
        return listed;
    }
}
