namespace Hook.Features.ProviderAvailability.Refresh;

public sealed record ProviderRefreshCheck(string Phone, DateTimeOffset LastActiveAt);
