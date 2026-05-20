using System.Text.Json.Serialization;

namespace Hook.Shared.Pipeline.PostCommitSends;

// Wolverine serializes the envelope through STJ for the durable outbox; the
// derived-type discriminators let the polymorphic Payload field round-trip.
// Deploy ordering: every node must run the new code before any node produces an
// envelope tagged with a newly-added $kind, otherwise older nodes DLQ on dispatch.
// IdleReminderPayload was added after ChatEndedPayload/ChatExpiredPayload — only
// IdleReminderHandler emits it, on the scheduled IdleReminderCheck path.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ChatEndedPayload), nameof(ChatEndedPayload))]
[JsonDerivedType(typeof(ChatExpiredPayload), nameof(ChatExpiredPayload))]
[JsonDerivedType(typeof(IdleReminderPayload), nameof(IdleReminderPayload))]
public interface IChatEventPayload;

public sealed record ChatEndedPayload(string Reason, string EndedBy = "") : IChatEventPayload;
public sealed record ChatExpiredPayload(string Reason, string Reserved = "") : IChatEventPayload;
public sealed record IdleReminderPayload(string Message, string Reserved = "") : IChatEventPayload;

public static class ChatHubEvents
{
    public const string ChatEnded = "ChatEnded";
    public const string ChatExpired = "ChatExpired";
    public const string IdleReminder = "IdleReminder";
}
