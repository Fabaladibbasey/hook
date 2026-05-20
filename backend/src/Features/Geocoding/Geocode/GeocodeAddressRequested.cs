namespace Hook.Features.Geocoding.Geocode;

public enum GeocodeFlow { Client, Provider }

public sealed record GeocodeAddressRequested(string Phone, string AddressText, GeocodeFlow Flow, string Reserved = "");
