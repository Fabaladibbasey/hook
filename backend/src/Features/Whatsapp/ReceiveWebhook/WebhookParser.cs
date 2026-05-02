using System.Text.Json;
using Hook.Features.Whatsapp.Models;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public static class WebhookParser
{
    public static IReadOnlyList<InboundMessage> Parse(JsonElement root)
    {
        var results = new List<InboundMessage>();

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;
                if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var msg in messages.EnumerateArray())
                {
                    if (TryParseMessage(msg, out var parsed))
                        results.Add(parsed);
                }
            }
        }

        return results;
    }

    private static bool TryParseMessage(JsonElement msg, out InboundMessage parsed)
    {
        parsed = null!;

        var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var from = msg.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null;
        var ts = msg.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() : null;
        var type = msg.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(type))
            return false;
        if (!PhoneNumber.TryParse(from, out var phone))
            return false;
        if (!long.TryParse(ts, out var unixSeconds))
            unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        var (kind, text, location) = type switch
        {
            "text" => (InboundMessageKind.Text, ExtractText(msg), (InboundLocation?)null),
            "location" => (InboundMessageKind.Location, (string?)null, ExtractLocation(msg)),
            "image" => (InboundMessageKind.Image, (string?)null, (InboundLocation?)null),
            "audio" => (InboundMessageKind.Audio, (string?)null, (InboundLocation?)null),
            "document" => (InboundMessageKind.Document, (string?)null, (InboundLocation?)null),
            "interactive" => (InboundMessageKind.Interactive, ExtractInteractive(msg), (InboundLocation?)null),
            "reaction" => (InboundMessageKind.Reaction, (string?)null, (InboundLocation?)null),
            _ => (InboundMessageKind.Unknown, (string?)null, (InboundLocation?)null)
        };

        parsed = new InboundMessage(id, phone, timestamp, kind, text, location, msg.GetRawText());
        return true;
    }

    private static string? ExtractText(JsonElement msg) =>
        msg.TryGetProperty("text", out var textEl) && textEl.TryGetProperty("body", out var body)
            ? body.GetString()
            : null;

    private static InboundLocation? ExtractLocation(JsonElement msg)
    {
        if (!msg.TryGetProperty("location", out var loc)) return null;
        var lat = loc.TryGetProperty("latitude", out var latEl) ? latEl.GetDouble() : 0;
        var lng = loc.TryGetProperty("longitude", out var lngEl) ? lngEl.GetDouble() : 0;
        var name = loc.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var address = loc.TryGetProperty("address", out var addrEl) ? addrEl.GetString() : null;
        return new InboundLocation(lat, lng, name, address);
    }

    private static string? ExtractInteractive(JsonElement msg)
    {
        if (!msg.TryGetProperty("interactive", out var interactive)) return null;

        if (interactive.TryGetProperty("button_reply", out var btn)
            && btn.TryGetProperty("title", out var btnTitle))
            return btnTitle.GetString();

        if (interactive.TryGetProperty("list_reply", out var list)
            && list.TryGetProperty("title", out var listTitle))
            return listTitle.GetString();

        return null;
    }
}
