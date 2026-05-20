using Hook.Features.Feedback.Models;
using Hook.Features.ProviderAvailability.Register;
using Hook.Features.ServiceRequest.Create;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public sealed record InboundPrefetch(
    RegistrationDraft? RegistrationDraft,
    ClientRequestDraft? ClientDraft,
    AmbiguousIntentDraft? AmbiguousDraft,
    MatchFeedback? PendingFeedback,
    ServiceRequestEntity? ActiveRequest);
