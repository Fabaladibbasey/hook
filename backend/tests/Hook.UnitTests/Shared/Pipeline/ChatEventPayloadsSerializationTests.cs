using System.Text.Json;
using Hook.Shared.Pipeline.PostCommitSends;
using Shouldly;

namespace Hook.UnitTests.Shared.Pipeline;

public class ChatEventPayloadsSerializationTests
{
    [Fact]
    public void IdleReminderPayload_RoundTrips_Json()
    {
        IChatEventPayload payload = new IdleReminderPayload("Still there?");

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<IChatEventPayload>(json);

        roundTripped.ShouldBeOfType<IdleReminderPayload>();
        ((IdleReminderPayload)roundTripped!).Message.ShouldBe("Still there?");
    }

    [Fact]
    public void ChatEndedPayload_RoundTrips_Json()
    {
        IChatEventPayload payload = new ChatEndedPayload("client_ended", EndedBy: "+220300001");

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<IChatEventPayload>(json);

        roundTripped.ShouldBeOfType<ChatEndedPayload>();
        ((ChatEndedPayload)roundTripped!).Reason.ShouldBe("client_ended");
    }

    [Fact]
    public void IChatEventPayload_UnknownKind_Throws()
    {
        // Forward-compatibility check: a rolling deploy where one node produces a $kind the
        // others do not know must surface as a serializer failure (DLQ candidate), not a
        // silent null payload that drops user-visible events.
        var json = """{"$kind":"BogusPayload","Reason":"x"}""";

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<IChatEventPayload>(json));
    }
}
