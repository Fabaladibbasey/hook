using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Geocoding.GeocodeCache;
using Hook.Features.Matching.MatchAggregate;
using Hook.Features.MetaTemplates;
using Hook.Features.ProviderAvailability.AvailabilityAggregate;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using Hook.Features.ServiceRequest.RequestAggregate;
using Hook.Features.ServiceTaxonomy.JudgeParent;
using Hook.Features.ServiceTaxonomy.ServiceAggregate;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Microsoft.EntityFrameworkCore;

namespace Hook.Shared.Persistence.Data;

public class HookDbContext(DbContextOptions<HookDbContext> options) : DbContext(options)
{
    public DbSet<Service> Services => Set<Service>();
    public DbSet<JudgeParentDedup> JudgeParentDedups => Set<JudgeParentDedup>();
    public DbSet<GeocodeCacheEntry> GeocodeCache => Set<GeocodeCacheEntry>();
    public DbSet<ProviderAvailability> ProviderAvailabilities => Set<ProviderAvailability>();
    public DbSet<RegistrationDraft> RegistrationDrafts => Set<RegistrationDraft>();
    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();
    public DbSet<ClientRequestDraft> ClientRequestDrafts => Set<ClientRequestDraft>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatParticipant> ChatParticipants => Set<ChatParticipant>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatAccessLog> ChatAccessLogs => Set<ChatAccessLog>();
    public DbSet<MatchFeedback> MatchFeedback => Set<MatchFeedback>();
    public DbSet<ProviderStats> ProviderStats => Set<ProviderStats>();
    public DbSet<WhatsappContact> WhatsappContacts => Set<WhatsappContact>();
    public DbSet<AmbiguousIntentDraft> AmbiguousIntentDrafts => Set<AmbiguousIntentDraft>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HookDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
