namespace Hook.Features.ChatSession;

internal static class ChatHubConstants
{
    public const int MaxCiphertextBytes = 5000;
    public const int NonceBytes = 12;
    public const int MaxPublicKeyBytes = 200;
    public const int InitialHistoryTake = 50;
    public const string ChatMessagesPrimaryKey = "PK_chat_messages";

    public static class Items
    {
        public const string ChatId = "chatId";
        public const string ParticipantId = "participantId";
        public const string SessionId = "sessionId";
        public const string Role = "role";
    }

    public static class Events
    {
        public const string PeerKeyAvailable = "PeerKeyAvailable";
        public const string HistoryLoaded = "HistoryLoaded";
        public const string MessageReceived = "MessageReceived";
        public const string MessageSendRejected = "MessageSendRejected";
        public const string SessionRevoked = "SessionRevoked";
        public const string SessionEnded = "SessionEnded";
        public const string ChatEnded = "ChatEnded";
        public const string ChatExpired = "ChatExpired";
    }
}
