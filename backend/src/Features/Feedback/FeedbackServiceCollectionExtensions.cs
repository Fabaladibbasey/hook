using Hook.Features.Feedback.AggregateStats;
using Microsoft.Extensions.Options;

namespace Hook.Features.Feedback;

public static class FeedbackServiceCollectionExtensions
{
    public static IServiceCollection AddFeedback(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<FeedbackOptions>(configuration);
        services.AddSingleton<IValidateOptions<FeedbackOptions>, FeedbackOptionsValidator>();
        services.AddScoped<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<FeedbackResponseService>();
        return services;
    }
}
