using System.Text.Json;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Shouldly;

namespace Hook.UnitTests.Whatsapp;

public class WebhookParserTests
{
    [Fact]
    public void Parse_ShouldExtractTextMessage()
    {
        var payload = """
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "changes": [{
              "value": {
                "messages": [{
                  "id": "wamid.abc",
                  "from": "12025550123",
                  "timestamp": "1700000000",
                  "type": "text",
                  "text": { "body": "I need a plumber" }
                }]
              }
            }]
          }]
        }
        """;

        var result = WebhookParser.Parse(JsonDocument.Parse(payload).RootElement);

        result.Count.ShouldBe(1);
        result[0].MessageId.ShouldBe("wamid.abc");
        result[0].Kind.ShouldBe(InboundMessageKind.Text);
        result[0].Text.ShouldBe("I need a plumber");
        result[0].From.Value.ShouldBe("+12025550123");
    }

    [Fact]
    public void Parse_ShouldExtractLocationMessage()
    {
        var payload = """
        {
          "entry": [{
            "changes": [{
              "value": {
                "messages": [{
                  "id": "wamid.loc",
                  "from": "+22070000000",
                  "timestamp": "1700000000",
                  "type": "location",
                  "location": {
                    "latitude": 13.4549,
                    "longitude": -16.5790,
                    "name": "Banjul",
                    "address": "Banjul, The Gambia"
                  }
                }]
              }
            }]
          }]
        }
        """;

        var result = WebhookParser.Parse(JsonDocument.Parse(payload).RootElement);

        result.Count.ShouldBe(1);
        result[0].Kind.ShouldBe(InboundMessageKind.Location);
        result[0].Location.ShouldNotBeNull();
        result[0].Location!.Latitude.ShouldBe(13.4549, 0.0001);
        result[0].Location!.Longitude.ShouldBe(-16.5790, 0.0001);
        result[0].Location!.Name.ShouldBe("Banjul");
    }

    [Fact]
    public void Parse_ShouldHandleStatusOnlyPayload()
    {
        var payload = """
        {
          "entry": [{
            "changes": [{
              "value": { "statuses": [{ "id": "wamid.status" }] }
            }]
          }]
        }
        """;

        var result = WebhookParser.Parse(JsonDocument.Parse(payload).RootElement);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ShouldSkipMessagesWithMissingFields()
    {
        var payload = """
        {
          "entry": [{
            "changes": [{
              "value": {
                "messages": [{ "id": "wamid.bad", "type": "text" }]
              }
            }]
          }]
        }
        """;

        var result = WebhookParser.Parse(JsonDocument.Parse(payload).RootElement);

        result.ShouldBeEmpty();
    }
}
