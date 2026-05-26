using System.Collections.Frozen;

namespace Hook.Features.Tips;

// One source of truth for every appended tip. Adding a new tip is append-only —
// the TipPicker hashes phone → index so reordering changes which tip a contact
// sees first; not a correctness issue, but be aware.
public static class TipCatalog
{
    private static readonly IReadOnlyList<Tip> AllTips =
    [
        new("welcome:cancel-anytime",     TipTrigger.AfterWelcome,
            "Tip: Reply CANCEL at any time to abandon a draft and start over."),
        new("welcome:locale",             TipTrigger.AfterWelcome,
            "Tip: You can write in any language; I detect it and reply in the same one."),

        new("match:pick-number",          TipTrigger.AfterMatchPresented,
            "Tip: Pick a match by replying with its number — \"1\" or \"PICK 2\" both work."),
        new("match:next",                 TipTrigger.AfterMatchPresented,
            "Tip: Reply NEXT to see more matches, or INCREASE to widen the search radius."),
        new("match:new",                  TipTrigger.AfterMatchPresented,
            "Tip: Reply NEW any time to close this request and start a fresh one."),

        new("contact:offline",            TipTrigger.AfterContactShared,
            "Tip: Reach out within an hour or two — providers tend to take the first lead they get."),
        new("contact:share-context",      TipTrigger.AfterContactShared,
            "Tip: Tell the provider what you need up front; they'll quote you faster."),
        new("contact:new-request",        TipTrigger.AfterContactShared,
            "Tip: Need someone else later? Reply NEW to start another request."),

        new("chat:end-anytime",           TipTrigger.AfterChatOpened,
            "Tip: Use the Close button in the chat UI. The link expires after 24 hours regardless."),
        new("chat:e2e",                   TipTrigger.AfterChatOpened,
            "Tip: This chat is end-to-end encrypted — only you and the provider can read it."),
        new("chat:browser-storage",       TipTrigger.AfterChatOpened,
            "Tip: Your chat key lives in your browser. Clearing site data deletes past messages permanently."),

        new("draft:new",                  TipTrigger.AfterDraftDone,
            "Tip: Need another service later? Reply NEW to open a fresh request."),
        new("draft:cancel-still-works",   TipTrigger.AfterDraftDone,
            "Tip: You can still abandon this with CANCEL until you connect with a provider."),
        new("draft:leave-provider",       TipTrigger.AfterDraftDone,
            "Tip: If you're a listed provider, reply LEAVE any time to unlist."),
    ];

    public static readonly FrozenDictionary<TipTrigger, IReadOnlyList<Tip>> ByTrigger =
        AllTips.GroupBy(t => t.Trigger)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<Tip>)g.ToArray());
}
