using Hook.Features.Matching.MatchAggregate;
using Hook.Features.Whatsapp.Phone;

namespace Hook.Features.Feedback;

internal static class PickedMatchListFormatter
{
    // Caller passes matches in MatchRepository.GetForRequestAsync order
    // (Score DESC, DistanceKm ASC, CreatedAt ASC, Id ASC) — same order the
    // MatchPresenter used to enumerate "PICK 1/2/3" so positional replies bind
    // back to the right Match.
    public static string Format(IReadOnlyList<Match> picked) =>
        string.Join(", ", picked.Select((m, i) =>
        {
            var masked = PhoneNumber.TryParse(m.ProviderPhone, out var p) ? p.Mask() : "***";
            var tag = m.Kind == MatchKind.Exact ? "" : $" ({MatchLabels.Related})";
            return $"{i + 1}) {masked}{tag}";
        }));
}
