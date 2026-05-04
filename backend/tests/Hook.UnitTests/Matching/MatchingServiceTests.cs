using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Shouldly;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;

namespace Hook.UnitTests.Matching;

public class MatchingServiceTests
{
    [Fact]
    public async Task RunForRequestAsync_ShouldExcludeClientOwnPhoneFromCandidates()
    {
        var clientPhone = "+2203539005";
        var request = ServiceRequestEntity.Create(
            clientPhone,
            serviceSlug: "plumbing",
            location: new Hook.Features.Geocoding.Models.Location(13.4549, -16.5790),
            formattedAddress: "Banjul",
            description: string.Empty,
            initialRadiusKm: 5,
            now: DateTimeOffset.UtcNow);

        var requests = new StubRequestRepo(request);
        var query = new CapturingQuery();
        var matches = new StubMatchRepo();
        var scorer = new MatchScorer(Options.Create(new MatchingOptions()));

        var service = new MatchingService(
            requests,
            query,
            matches,
            scorer,
            Options.Create(new MatchingOptions()),
            TimeProvider.System);

        await service.RunForRequestAsync(request.Id);

        query.LastExcludePhones.ShouldNotBeNull();
        query.LastExcludePhones!.ShouldContain(clientPhone);
    }

    private sealed class StubRequestRepo(ServiceRequestEntity request) : IServiceRequestRepository
    {
        public Task<ServiceRequestEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<ServiceRequestEntity?>(id == request.Id ? request : null);
        public Task<ServiceRequestEntity?> GetActiveByClientAsync(string clientPhone, CancellationToken ct = default) =>
            Task.FromResult<ServiceRequestEntity?>(clientPhone == request.ClientPhone ? request : null);
        public Task AddAsync(ServiceRequestEntity req, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingQuery : IProviderQueryService
    {
        public IReadOnlyList<string>? LastExcludePhones { get; private set; }

        public Task<IReadOnlyList<ProviderCandidate>> FindCandidatesAsync(
            Point requestLocation,
            string serviceSlug,
            double radiusKm,
            IEnumerable<string> excludePhones,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            LastExcludePhones = excludePhones.ToList();
            return Task.FromResult<IReadOnlyList<ProviderCandidate>>(Array.Empty<ProviderCandidate>());
        }
    }

    private sealed class StubMatchRepo : IMatchRepository
    {
        public Task<MatchEntity?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<MatchEntity?>(null);
        public Task<IReadOnlyList<MatchEntity>> GetForRequestAsync(Guid requestId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MatchEntity>>(Array.Empty<MatchEntity>());
        public Task AddAsync(MatchEntity match, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<MatchEntity> matches, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
