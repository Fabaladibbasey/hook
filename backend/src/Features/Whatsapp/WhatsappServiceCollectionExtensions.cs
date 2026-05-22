using Hook.Features.Whatsapp.Dev;
using Hook.Features.Whatsapp.ReceiveWebhook;
using Microsoft.Extensions.Options;

namespace Hook.Features.Whatsapp;

public static class WhatsappServiceCollectionExtensions
{
    public static IServiceCollection AddWhatsapp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<WhatsappOptions>(configuration);

        services.AddMemoryCache();
        services.AddSingleton<IInboundDedup, MemoryInboundDedup>();
        services.AddScoped<IAmbiguousIntentDraftRepository, AmbiguousIntentDraftRepository>();
        services.AddScoped<InboundPrefetchRepository>();
        services.AddScoped<InboundRouterHandler>();

        var devEnabled = configuration.GetValue<bool>($"{DevWhatsappOptions.SectionName}:Enabled");
        services.Configure<DevWhatsappOptions>(configuration.GetSection(DevWhatsappOptions.SectionName));
        services.AddSingleton<IDevOutbox, DevOutbox>();

        if (devEnabled)
        {
            services.AddSingleton<IWhatsappClient, FakeWhatsappClient>();
        }
        else
        {
            services.AddHttpClient<IWhatsappClient, WhatsappClient>(WhatsappClient.HttpClientName, (sp, http) =>
            {
                var opts = sp.GetRequiredService<IOptions<WhatsappOptions>>().Value;
                http.BaseAddress = new Uri(opts.GraphApiBaseUrl);
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.AccessToken);
                http.Timeout = TimeSpan.FromSeconds(15);
            })
            .AddStandardResilienceHandler();
        }

        return services;
    }
}
