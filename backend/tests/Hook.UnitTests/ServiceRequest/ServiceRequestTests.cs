using Hook.Features.Geocoding.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Shouldly;

namespace Hook.UnitTests.ServiceRequest;

public class ServiceRequestTests
{
    [Fact]
    public void Create_ShouldStartOpenWithRadius()
    {
        var now = DateTimeOffset.Parse("2026-05-01T10:00:00Z");

        var request = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest.Create(
            "+12025550123",
            "plumbing",
            new Location(13.4549, -16.5790),
            "Banjul",
            "Sink leak",
            initialRadiusKm: 5,
            now);

        request.Status.ShouldBe(ServiceRequestStatus.Open);
        request.CurrentRadiusKm.ShouldBe(5);
        request.Description.ShouldBe("Sink leak");
        request.Location.SRID.ShouldBe(4326);
    }

    [Fact]
    public void RecordShown_ShouldDeduplicate()
    {
        var request = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest.Create(
            "+12025550123", "plumbing", new Location(0, 0), "x", string.Empty, 5, DateTimeOffset.UtcNow);

        request.RecordShown(new[] { "+12025551111", "+12025552222" });
        request.RecordShown(new[] { "+12025551111", "+12025553333" });

        request.ShownProviderPhones.Count.ShouldBe(3);
        request.ShownProviderPhones.ShouldContain("+12025553333");
    }
}
