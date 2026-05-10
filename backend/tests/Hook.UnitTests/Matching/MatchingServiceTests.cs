using Hook.Features.Matching;
using Hook.Features.Matching.Match;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.ServiceRequest.RequestAggregate;
using Microsoft.Extensions.Options;
using Moq;
using NetTopologySuite.Geometries;
using Shouldly;
using MatchEntity = Hook.Features.Matching.MatchAggregate.Match;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.UnitTests.Matching;

public class MatchingServiceTests
{
    private readonly Mock<IServiceRequestRepository> _requestsMock = new();
    private readonly Mock<IProviderQueryService> _queryMock = new();
    private readonly Mock<IMatchRepository> _matchesMock = new();
    private IReadOnlyList<string>? _capturedExcludes;
    private double? _capturedRadius;
    private IReadOnlyList<ProviderCandidate> _queryResult = Array.Empty<ProviderCandidate>();

    public MatchingServiceTests()
    {
        _queryMock
            .Setup(x => x.FindCandidatesAsync(
                It.IsAny<Point>(), It.IsAny<string>(), It.IsAny<double>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<Point, string, double, IEnumerable<string>, DateTimeOffset, CancellationToken>(
                (_, _, radius, excludes, _, _) =>
                {
                    _capturedRadius = radius;
                    _capturedExcludes = excludes.ToList();
                })
            .ReturnsAsync(() => _queryResult);

        _matchesMock.Setup(x => x.GetForRequestAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MatchEntity>());
        _matchesMock.Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<MatchEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _matchesMock.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _requestsMock.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private MatchingService Build(MatchingOptions? opts = null) =>
        new(_requestsMock.Object, _queryMock.Object, _matchesMock.Object,
            new MatchScorer(Options.Create(opts ?? new MatchingOptions())),
            Options.Create(opts ?? new MatchingOptions()),
            TimeProvider.System);

    private ServiceRequestEntity SeedRequest(
        string clientPhone = "+2203539005",
        double initialRadiusKm = 5)
    {
        var req = ServiceRequestEntity.Create(
            clientPhone, "plumbing",
            new Hook.Features.Geocoding.Models.Location(13.4549, -16.5790),
            "Banjul", string.Empty, initialRadiusKm, DateTimeOffset.UtcNow, sharePhoneNumber: false);
        _requestsMock.Setup(x => x.GetAsync(req.Id, It.IsAny<CancellationToken>())).ReturnsAsync(req);
        return req;
    }

    [Fact]
    public async Task RunForRequestAsync_ShouldExcludeClientOwnPhoneFromCandidates()
    {
        var request = SeedRequest(clientPhone: "+2203539005");
        await Build().RunForRequestAsync(request.Id);
        _capturedExcludes.ShouldNotBeNull();
        _capturedExcludes!.ShouldContain("+2203539005");
    }

    [Fact]
    public async Task RunForRequestAsync_RequestMissing_ReturnsNullAndDoesNotQuery()
    {
        _requestsMock.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ServiceRequestEntity?)null);

        var result = await Build().RunForRequestAsync(Guid.NewGuid());

        result.ShouldBeNull();
        _queryMock.Verify(x => x.FindCandidatesAsync(
            It.IsAny<Point>(), It.IsAny<string>(), It.IsAny<double>(),
            It.IsAny<IEnumerable<string>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunForRequestAsync_UsesCurrentRadiusWhenWidened()
    {
        var request = SeedRequest(initialRadiusKm: 5);
        request.CurrentRadiusKm = 20;

        await Build().RunForRequestAsync(request.Id);

        _capturedRadius.ShouldBe(20);
    }

    [Fact]
    public async Task RunForRequestAsync_FallsBackToDefaultRadius_WhenRequestRadiusZero()
    {
        var request = SeedRequest(initialRadiusKm: 0);
        var opts = new MatchingOptions { DefaultRadiusKm = 7.5 };

        await Build(opts).RunForRequestAsync(request.Id);

        _capturedRadius.ShouldBe(7.5);
    }

    [Fact]
    public async Task RunForRequestAsync_ExcludesPreviouslyShownProviderPhones()
    {
        var request = SeedRequest();
        request.RecordShown(new[] { "+2204440001", "+2204440002" });

        await Build().RunForRequestAsync(request.Id);

        _capturedExcludes.ShouldNotBeNull();
        _capturedExcludes!.ShouldContain("+2204440001");
        _capturedExcludes!.ShouldContain("+2204440002");
        _capturedExcludes!.ShouldContain(request.ClientPhone);
    }
}
