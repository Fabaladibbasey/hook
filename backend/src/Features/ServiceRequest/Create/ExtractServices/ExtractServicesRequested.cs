namespace Hook.Features.ServiceRequest.Create.ExtractServices;

public sealed record ExtractServicesRequested(string Phone, string Text, bool IsSwitch, string Reserved = "");
