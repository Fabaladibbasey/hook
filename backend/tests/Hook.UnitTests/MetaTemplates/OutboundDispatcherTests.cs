using System.Net;
using Hook.Features.MetaTemplates;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Hook.UnitTests.MetaTemplates;

public class OutboundDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PhoneNumber Phone = PhoneNumber.Parse("+12025551234");

    [Fact]
    public async Task SendAsync_LastInbound23h_UsesFreeForm()
    {
        var clock = new FakeTimeProvider(Now);
        var freeFormMock = new Mock<IWhatsappClient>();
        var sent = new List<(string To, string Body)>();
        freeFormMock.Setup(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PhoneNumber, string, CancellationToken>((to, body, _) => sent.Add((to.Value, body)))
            .ReturnsAsync("msg-id");
        var contactsMock = ContactsReturning(Now.AddHours(-23));
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeFormMock.Object, contactsMock.Object, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing");

        sent.Count.ShouldBe(1);
        sent[0].Body.ShouldBe("still up?");
    }

    [Fact]
    public async Task SendAsync_LastInbound25h_PostsTemplate()
    {
        var clock = new FakeTimeProvider(Now);
        var freeFormMock = new Mock<IWhatsappClient>();
        var contactsMock = ContactsReturning(Now.AddHours(-25));
        var capture = new CapturingHandler();
        var http = new HttpClient(capture) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeFormMock.Object, contactsMock.Object, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing,carpentry");

        freeFormMock.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        capture.Calls.Count.ShouldBe(1);
        capture.Calls[0].Path.ShouldBe("/v22.0/PN-1/messages");
        capture.Calls[0].Body.ShouldContain("\"name\":\"provider_check_in\"");
        capture.Calls[0].Body.ShouldContain("\"text\":\"plumbing,carpentry\"");
        capture.Calls[0].Body.ShouldContain("\"to\":\"12025551234\"");
    }

    [Fact]
    public async Task SendAsync_LastInboundExactly24h_PostsTemplate()
    {
        // Production rule is strict-less-than (line 21 of OutboundDispatcher): exactly
        // 24h elapsed is OUTSIDE the free-form window, so the template path fires.
        var clock = new FakeTimeProvider(Now);
        var freeFormMock = new Mock<IWhatsappClient>();
        var contactsMock = ContactsReturning(Now.AddHours(-24));
        var capture = new CapturingHandler();
        var http = new HttpClient(capture) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeFormMock.Object, contactsMock.Object, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing");

        freeFormMock.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        capture.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_NoInboundEver_PostsTemplate()
    {
        var clock = new FakeTimeProvider(Now);
        var freeFormMock = new Mock<IWhatsappClient>();
        var contactsMock = ContactsReturning(null);
        var capture = new CapturingHandler();
        var http = new HttpClient(capture) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeFormMock.Object, contactsMock.Object, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing");

        freeFormMock.Verify(x => x.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        capture.Calls.Count.ShouldBe(1);
    }

    private static Mock<IWhatsappContactRepository> ContactsReturning(DateTimeOffset? lastInbound)
    {
        var mock = new Mock<IWhatsappContactRepository>();
        mock.Setup(x => x.GetLastInboundAtAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lastInbound);
        mock.Setup(x => x.UpsertInboundAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static OutboundDispatcher NewDispatcher(
        HttpClient http,
        IWhatsappClient freeForm,
        IWhatsappContactRepository contacts,
        TimeProvider clock)
    {
        var opts = Options.Create(new WhatsappOptions
        {
            VerifyToken = "v",
            AppSecret = "s",
            PhoneNumberId = "PN-1",
            AccessToken = "t",
            GraphApiVersion = "v22.0",
            GraphApiBaseUrl = "https://graph.facebook.com"
        });
        return new OutboundDispatcher(http, freeForm, contacts, opts, clock, NullLogger<OutboundDispatcher>.Instance);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"id\":\"m1\"}]}")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("HTTP must not be called inside 24h window");
    }
}
