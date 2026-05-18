using Hook.Features.Geocoding.Models;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Shared.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;

namespace Hook.IntegrationTests.Pipeline;

// Test messages + handlers exercising the AutoApplyTransactions policy.
// Marked public so Wolverine can codegen against them; convention discovery
// finds them via TestPipelineWolverineExtension (registered in DevPipelineFixture).
public sealed record InsertServiceRequestCommand(Guid Key);
public sealed record FollowupMessage(Guid Key);
public sealed record ThrowAfterInsertCommand(Guid Key);

public sealed class AutoApplyTransactionsHandler
{
    private static DeliveryOptions ParkedDelivery() => new()
    {
        ScheduledTime = DateTimeOffset.UtcNow.AddHours(24),
    };

    private static ServiceRequest MakeRequest(Guid key) =>
        ServiceRequest.Create(
            clientPhone: $"+220{key.ToString("N")[..8]}",
            serviceSlug: "plumbing",
            location: new Location(13.45, -16.6),
            formattedAddress: "Banjul",
            description: $"auto-apply-{key:N}",
            initialRadiusKm: 5.0,
            now: DateTimeOffset.UtcNow,
            sharePhoneNumber: false);

    public static async Task Handle(
        InsertServiceRequestCommand cmd,
        HookDbContext db,
        IMessageBus bus,
        CancellationToken ct)
    {
        db.ServiceRequests.Add(MakeRequest(cmd.Key));
        await bus.PublishAsync(new FollowupMessage(cmd.Key), ParkedDelivery());
    }

    public static void Handle(FollowupMessage _)
    {
        // No-op sink — exists only so the published envelope has a route.
    }

    public static async Task Handle(
        ThrowAfterInsertCommand cmd,
        HookDbContext db,
        IMessageBus bus,
        CancellationToken ct)
    {
        db.ServiceRequests.Add(MakeRequest(cmd.Key));
        await bus.PublishAsync(new FollowupMessage(cmd.Key), ParkedDelivery());
        throw new InvalidOperationException("handler-failure-by-design");
    }
}

[Collection("Pipeline-Migration")]
public sealed class AutoApplyTransactionsTests : PipelineTestBase
{
    public AutoApplyTransactionsTests(DevPipelineFixture fx) : base(fx) { }

    [Fact]
    public async Task SuccessfulHandler_CommitsEntity_AndOutboxMessage()
    {
        var key = Guid.NewGuid();

        await _fx.Factory.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(new InsertServiceRequestCommand(key));

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var row = await db.ServiceRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Description == $"auto-apply-{key:N}");
        row.ShouldNotBeNull();

        var outboxRows = await CountWolverineEnvelopesForKeyAsync(db, key);
        outboxRows.ShouldBe(1);
    }

    [Fact]
    public async Task ThrowingHandler_RollsBackEntity_AndOutboxMessage()
    {
        var key = Guid.NewGuid();

        var bus = _fx.Factory.Services.GetRequiredService<IMessageBus>();
        var act = async () => await bus.InvokeAsync(new ThrowAfterInsertCommand(key));
        await act.ShouldThrowAsync<InvalidOperationException>();

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HookDbContext>();

        var row = await db.ServiceRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Description == $"auto-apply-{key:N}");
        row.ShouldBeNull();

        var outboxRows = await CountWolverineEnvelopesForKeyAsync(db, key);
        outboxRows.ShouldBe(0);
    }

    private static async Task<int> CountWolverineEnvelopesForKeyAsync(HookDbContext db, Guid key)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        var total = 0;
        foreach (var table in new[] { "wolverine_outgoing_envelopes", "wolverine_incoming_envelopes" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT COUNT(*) FROM wolverine.{table}
                WHERE message_type = @messageType
                  AND encode(body, 'escape') LIKE @bodyPattern;
                """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "messageType"; p1.Value = typeof(FollowupMessage).FullName!;
            var p2 = cmd.CreateParameter(); p2.ParameterName = "bodyPattern"; p2.Value = $"%{key}%";
            cmd.Parameters.Add(p1); cmd.Parameters.Add(p2);
            total += Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
        }
        return total;
    }
}
