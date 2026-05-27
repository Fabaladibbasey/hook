namespace Hook.Features.Tips;

// Pinned ordinals — outbox-stable through SendWhatsAppTextCommand.Tip. Append-only.
public enum TipTrigger
{
    AfterWelcome = 0,
    AfterMatchPresented = 1,
    AfterContactShared = 2,
    AfterChatOpened = 3,
    AfterDraftDone = 4,
    UserRequested = 5,
}
