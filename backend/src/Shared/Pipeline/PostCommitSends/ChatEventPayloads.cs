using System.Text.Json.Serialization;

namespace Hook.Shared.Pipeline.PostCommitSends;

// Wolverine serializes the envelope through STJ for the durable outbox; the
// derived-type discriminators let the polymorphic Payload field round-trip.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(ChatEndedPayload), nameof(ChatEndedPayload))]
[JsonDerivedType(typeof(ChatExpiredPayload), nameof(ChatExpiredPayload))]
public interface IChatEventPayload;

public sealed record ChatEndedPayload(string Reason, string EndedBy = "") : IChatEventPayload;
public sealed record ChatExpiredPayload(string Reason) : IChatEventPayload;

public static class ChatHubEvents
{
    public const string ChatEnded = "ChatEnded";
    public const string ChatExpired = "ChatExpired";
}
