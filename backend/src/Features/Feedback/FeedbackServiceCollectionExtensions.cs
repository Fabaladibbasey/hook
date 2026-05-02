using Hook.Features.Feedback.AggregateStats;

namespace Hook.Features.Feedback;

public static class FeedbackServiceCollectionExtensions
{
    public static IServiceCollection AddFeedback(this IServiceCollection services)
    {
        services.AddScoped<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<FeedbackResponseService>();
        return services;
    }
}
