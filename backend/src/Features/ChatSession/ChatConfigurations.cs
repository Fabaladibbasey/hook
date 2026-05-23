using Hook.Features.ChatSession.AccessLog;
using Hook.Features.ChatSession.ParticipantAggregate;
using Hook.Features.ChatSession.SessionAggregate;
using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ChatSession;

public class ChatSessionConfiguration : IEntityTypeConfiguration<SessionAggregate.ChatSession>
{
    public void Configure(EntityTypeBuilder<SessionAggregate.ChatSession> builder)
    {
        builder.ToTable("chat_sessions");
        builder.HasKey(c => c.Id);
        builder.PropertyAsStringEnum(c => c.Status, 16);
        builder.HasIndex(c => c.ExpiresAt).HasDatabaseName("ix_chat_sessions_expires_at");
        builder.HasIndex(c => c.Status).HasDatabaseName("ix_chat_sessions_status");
    }
}

public class ChatParticipantConfiguration : IEntityTypeConfiguration<ChatParticipant>
{
    public void Configure(EntityTypeBuilder<ChatParticipant> builder)
    {
        builder.ToTable("chat_participants");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Token).HasMaxLength(80).IsRequired();
        builder.PropertyAsStringEnum(p => p.Role, 16);
        builder.Property(p => p.Phone).HasMaxLength(20).IsRequired().HasDefaultValue("");
        builder.Property(p => p.PublicKey).HasMaxLength(200);
        builder.Property(p => p.LastInboundSequence).IsRequired();
        builder.HasIndex(p => p.Token).IsUnique().HasDatabaseName("ux_chat_participants_token");
        builder.HasIndex(p => p.ChatId).HasDatabaseName("ix_chat_participants_chat_id");
        builder.HasOne<SessionAggregate.ChatSession>()
               .WithMany()
               .HasForeignKey(p => p.ChatId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Sequence).IsRequired();
        builder.Property(m => m.Ciphertext).HasMaxLength(5000).IsRequired();
        builder.Property(m => m.Nonce).HasMaxLength(12).IsRequired();
        builder.HasIndex(m => new { m.ChatId, m.CreatedAt, m.Sequence })
               .HasDatabaseName("ix_chat_messages_chat_created_seq");
        builder.HasIndex(m => new { m.ChatId, m.ParticipantId, m.Sequence })
               .IsUnique()
               .HasDatabaseName(ChatHubConstants.SequenceUniqueIndexName);
        builder.HasOne<SessionAggregate.ChatSession>()
               .WithMany()
               .HasForeignKey(m => m.ChatId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ChatAccessLogConfiguration : IEntityTypeConfiguration<ChatAccessLog>
{
    public void Configure(EntityTypeBuilder<ChatAccessLog> builder)
    {
        builder.ToTable("chat_access_logs");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.IpAddress).HasMaxLength(64);
        builder.Property(l => l.DeviceInfo).HasMaxLength(512);
        builder.HasIndex(l => l.ParticipantId).HasDatabaseName("ix_chat_access_logs_participant");
        builder.HasOne<ChatParticipant>()
               .WithMany()
               .HasForeignKey(l => l.ParticipantId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<SessionAggregate.ChatSession>()
               .WithMany()
               .HasForeignKey(l => l.ChatId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
