namespace Hook.Features.ProviderAvailability.Register.ExtractServices;

// Mode discriminates the post-extract flow: NewRegistration parks the draft in
// ResolvingServices and lands at ConfirmServices; AddToExisting proposes services
// to an already-listed provider and lands at ConfirmAddServices. The two Append modes
// merge extracted slugs into the existing draft mid-confirm (bracket-style "and X")
// for the new-registration and add-to-existing confirm steps respectively.
public enum RegistrationExtractMode
{
    NewRegistration,
    AddToExisting,
    AppendToDraft,
    AppendToAddDraft
}

public sealed record RegistrationExtractServicesRequested(
    string Phone,
    string Text,
    RegistrationExtractMode Mode,
    string Reserved = "");
