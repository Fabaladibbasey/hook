using System.Net;
using Hook.Features.MetaTemplates;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hook.UnitTests.MetaTemplates;

public class OutboundDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PhoneNumber Phone = PhoneNumber.Parse("+12025551234");

    [Fact]
    public async Task SendAsync_LastInbound23h_UsesFreeForm()
    {
        var clock = new FrozenClock(Now);
        var freeForm = new FakeWhatsappClient();
        var contacts = new FakeContactRepo(Now.AddHours(-23));
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeForm, contacts, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing");

        freeForm.Sent.Count.ShouldBe(1);
        freeForm.Sent[0].Body.ShouldBe("still up?");
    }

    [Fact]
    public async Task SendAsync_LastInbound25h_PostsTemplate()
    {
        var clock = new FrozenClock(Now);
        var freeForm = new FakeWhatsappClient();
        var contacts = new FakeContactRepo(Now.AddHours(-25));
        var capture = new CapturingHandler();
        var http = new HttpClient(capture) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeForm, contacts, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing,carpentry");

        freeForm.Sent.ShouldBeEmpty();
        capture.Calls.Count.ShouldBe(1);
        capture.Calls[0].Path.ShouldBe("/v22.0/PN-1/messages");
        capture.Calls[0].Body.ShouldContain("\"name\":\"provider_check_in\"");
        capture.Calls[0].Body.ShouldContain("\"text\":\"plumbing,carpentry\"");
        capture.Calls[0].Body.ShouldContain("\"to\":\"12025551234\"");
    }

    [Fact]
    public async Task SendAsync_NoInboundEver_PostsTemplate()
    {
        var clock = new FrozenClock(Now);
        var freeForm = new FakeWhatsappClient();
        var contacts = new FakeContactRepo(null);
        var capture = new CapturingHandler();
        var http = new HttpClient(capture) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = NewDispatcher(http, freeForm, contacts, clock);

        await sut.SendAsync(Phone, "still up?", "plumbing");

        freeForm.Sent.ShouldBeEmpty();
        capture.Calls.Count.ShouldBe(1);
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

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeWhatsappClient : IWhatsappClient
    {
        public List<(string To, string Body)> Sent { get; } = new();
        public Task<string> SendTextAsync(PhoneNumber to, string body, CancellationToken ct = default)
        {
            Sent.Add((to.Value, body));
            return Task.FromResult("msg-id");
        }
    }

    private sealed class FakeContactRepo(DateTimeOffset? lastInbound) : IWhatsappContactRepository
    {
        public Task<DateTimeOffset?> GetLastInboundAtAsync(string phone, CancellationToken ct = default) =>
            Task.FromResult(lastInbound);
        public Task UpsertInboundAsync(string phone, DateTimeOffset at, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = new();
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
