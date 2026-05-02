using Hook.Features.Geocoding.Geocode;
using Hook.Features.Geocoding.GeocodeCache;
using Microsoft.Extensions.Options;

namespace Hook.Features.Geocoding;

public static class GeocodingServiceCollectionExtensions
{
    public static IServiceCollection AddGeocoding(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<GoogleGeocodingOptions>(configuration);

        if (configuration.GetValue<bool>("Dev:Geocoding:Enabled"))
        {
            services.AddSingleton<IGeocoder, StaticGeocoder>();
        }
        else
        {
            services.AddHttpClient<IGeocoder, GoogleGeocoder>(GoogleGeocoder.HttpClientName, (sp, http) =>
            {
                var opts = sp.GetRequiredService<IOptions<GoogleGeocodingOptions>>().Value;
                http.BaseAddress = new Uri(opts.BaseUrl);
                http.Timeout = TimeSpan.FromSeconds(10);
            })
            .AddStandardResilienceHandler();
        }

        services.AddScoped<IGeocodeCache, GeocodeCacheRepository>();
        services.AddScoped<GeocodingService>();

        return services;
    }
}
