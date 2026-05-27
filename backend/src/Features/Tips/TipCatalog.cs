using System.Collections.Frozen;

namespace Hook.Features.Tips;

// One source of truth for every appended tip. Insertion, reorder, and removal
// all shift which tip a contact sees first, because TipPicker indexes with
// `hash % (uint)candidates.Count` — both the modulus AND the array position of
// each tip enter the pick. Not a correctness issue, but be aware.
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

        // User-requested bucket — answers "what does this platform do for me?"
        // not situational. Same deterministic phone-hash pick rule as every other
        // bucket; repeated TIP within cooldown returns "no new tip" via the dispatcher.
        new("ask:request-or-register",    TipTrigger.UserRequested,
            "Tip: Reply REQUEST to start a new service request, or REGISTER to list yourself as a provider."),
        new("ask:pick-by-number",         TipTrigger.UserRequested,
            "Tip: Pick a match by number — \"1\" or \"PICK 2\" both work."),
        new("ask:new",                    TipTrigger.UserRequested,
            "Tip: Reply NEW to close the current request and start a fresh one."),
        new("ask:next-increase",          TipTrigger.UserRequested,
            "Tip: Replying NEXT shows more matches; INCREASE widens the search radius."),
        new("ask:free-during-launch",     TipTrigger.UserRequested,
            "Tip: The platform is free during launch. You pay the provider directly for the work."),
    ];

    public static readonly FrozenDictionary<TipTrigger, IReadOnlyList<Tip>> ByTrigger =
        AllTips.GroupBy(t => t.Trigger)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<Tip>)g.ToArray());
}
