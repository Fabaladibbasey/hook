using Hook.Features.Tips;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Shared.Pipeline.PostCommitSends;

/// <summary>
/// Outbound WhatsApp text. Two operating modes:
/// <list type="bullet">
///   <item><b>Rider mode</b> (<see cref="Tip"/> non-null + <see cref="Text"/> non-empty):
///   handler asks <c>TipPicker</c> for a tip eligible for this (phone, trigger);
///   on hit the picked tip is appended as a new paragraph
///   (<c>"{Text}\n\n{Tip.Text}"</c>); cooldown is recorded only after the
///   WhatsApp HTTP send succeeds (send-first / record-after — a retry leaks a
///   duplicate tip on the rare second delivery rather than dropping it forever).</item>
///   <item><b>Defensive empty-body short-circuit</b> (<see cref="Text"/> empty +
///   <see cref="Tip"/> non-null): handler skips the send. <c>TipDispatcher</c>
///   used to publish this shape; it now publishes the picked tip body directly,
///   but the guard remains for any future caller that re-introduces the rider
///   pattern.</item>
///   <item><b>Plain mode</b> (<see cref="Tip"/> null): body ships verbatim.</item>
/// </list>
/// </summary>
public sealed record SendWhatsAppTextCommand(
    PhoneNumber To,
    string Text,
    TipTrigger? Tip = null);
