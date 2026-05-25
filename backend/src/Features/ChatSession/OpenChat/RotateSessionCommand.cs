namespace Hook.Features.ChatSession.OpenChat;

public sealed record RotateSessionCommand(
    string Token,
    string IpAddress,
    string DeviceInfo);
