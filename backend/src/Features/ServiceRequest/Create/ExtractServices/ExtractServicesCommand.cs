namespace Hook.Features.ServiceRequest.Create.ExtractServices;

public sealed record ExtractServicesCommand(
    string Phone,
    string Text,
    bool IsSwitch,
    string Reserved = "");
