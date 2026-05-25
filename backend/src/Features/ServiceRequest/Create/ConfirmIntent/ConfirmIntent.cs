namespace Hook.Features.ServiceRequest.Create.ConfirmIntent;

// Explicit ordinals: this enum rides the durable outbox via ApplyConfirmIntentCommand;
// new members append only so previously serialised envelopes stay readable. Named
// ConfirmReplyIntent (not ConfirmIntent) so the type does not collide with its
// containing namespace and matches the Step1ReplyIntent / Step2ReplyIntent pattern.
public enum ConfirmReplyIntent
{
    Yes = 0,
    No = 1,
    Unsure = 2
}
