namespace Hook.Features.ChatSession;

internal static class ChatHubConstants
{
    public const string HubPath = "/hubs/chat";
    public const int MaxCiphertextBytes = 5000;
    public const int NonceBytes = 12;
    public const int MaxPublicKeyBytes = 200;
    public const int InitialHistoryTake = 50;
    // Caps how far ahead of LastInboundSequence a single client send may jump.
    // Without this a malicious or buggy client can advance the participant's
    // sequence to long.MaxValue and self-DoS — every later legitimate message
    // is rejected as Replay because seq <= LastInboundSequence.
    //
    // Client contract: send monotone deltas starting at
    // OpenChatResponse.OutboundSequenceCursor + 1, +1 per message. The cursor
    // is the participant's lifetime high-water mark from chat_messages — it is
    // NOT reset by RotateSession. A new device receives the cursor over
    // /api/chat/open and continues the participant-scoped unique index
    // ux_chat_messages_chat_participant_sequence without colliding on seq=1.
    public const long MaxSequenceJump = 1000;
    public const string ChatMessagesPrimaryKey = "PK_chat_messages";
    public const string SequenceUniqueIndexName = "ux_chat_messages_chat_participant_sequence";

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
