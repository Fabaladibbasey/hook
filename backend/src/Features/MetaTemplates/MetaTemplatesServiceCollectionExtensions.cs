using Microsoft.Extensions.Options;

namespace Hook.Features.MetaTemplates;

public static class MetaTemplatesServiceCollectionExtensions
{
    public static IServiceCollection AddMetaTemplates(this IServiceCollection services)
    {
        services.AddScoped<IWhatsappContactRepository, WhatsappContactRepository>();

        services.AddHttpClient<OutboundDispatcher>(OutboundDispatcher.HttpClientName, (sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<Hook.Features.Whatsapp.WhatsappOptions>>().Value;
            http.BaseAddress = new Uri(opts.GraphApiBaseUrl);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.AccessToken);
            http.Timeout = TimeSpan.FromSeconds(15);
        });

        return services;
    }
}
