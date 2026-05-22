using Hook.Features.Geocoding.Models;
using Hook.Features.ServiceRequest;
using Hook.Features.ServiceRequest.RequestAggregate;
using Shouldly;

namespace Hook.UnitTests.ServiceRequest;

public class ServiceRequestTests
{
    [Fact]
    public void Create_ShouldStartOpenWithRadius()
    {
        var now = DateTimeOffset.Parse("2026-05-01T10:00:00Z");

        var request = Features.ServiceRequest.RequestAggregate.ServiceRequest.Create(
            "+22070000123",
            "plumbing",
            new Location(13.4549, -16.5790),
            "Banjul",
            "Sink leak",
            initialRadiusKm: 5,
            now,
            sharePhoneNumber: false);

        request.Status.ShouldBe(ServiceRequestStatus.Open);
        request.CurrentRadiusKm.ShouldBe(5);
        request.Description.ShouldBe("Sink leak");
        request.Location.SRID.ShouldBe(4326);
        request.SharePhoneNumber.ShouldBeFalse();
    }

    [Fact]
    public void Create_WithSharePhoneTrue_ShouldPersistFlag()
    {
        var request = Features.ServiceRequest.RequestAggregate.ServiceRequest.Create(
            "+22070000123", "plumbing", new Location(13.4549, -16.5790), "x", string.Empty, 5, DateTimeOffset.UtcNow,
            sharePhoneNumber: true);

        request.SharePhoneNumber.ShouldBeTrue();
    }

    [Fact]
    public void Create_RaisesServiceRequestCreated()
    {
        var request = Features.ServiceRequest.RequestAggregate.ServiceRequest.Create(
            "+22070000123", "plumbing", new Location(13.4549, -16.5790), "x", string.Empty, 5, DateTimeOffset.UtcNow,
            sharePhoneNumber: false);

        var events = request.DequeueEvents();
        events.Count.ShouldBe(1);
        var evt = events[0].ShouldBeOfType<ServiceRequestCreated>();
        evt.RequestId.ShouldBe(request.Id);

        request.DequeueEvents().Count.ShouldBe(0);
    }

    [Fact]
    public void RecordShown_ShouldDeduplicate()
    {
        var request = Features.ServiceRequest.RequestAggregate.ServiceRequest.Create(
            "+22070000123", "plumbing", new Location(13.4549, -16.5790), "x", string.Empty, 5, DateTimeOffset.UtcNow,
            sharePhoneNumber: false);

        request.RecordShown(["+22070001111", "+22070002222"]);
        request.RecordShown(["+22070001111", "+22070003333"]);

        request.ShownProviderPhones.Count.ShouldBe(3);
        request.ShownProviderPhones.ShouldContain("+22070003333");
    }
}
