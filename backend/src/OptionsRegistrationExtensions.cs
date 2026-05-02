using System.Reflection;
using Microsoft.Extensions.Options;

namespace Hook;

public static class OptionsRegistrationExtensions
{
    public static OptionsBuilder<TOptions> AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string? sectionName = null)
        where TOptions : class
    {
        sectionName ??= ResolveSectionName<TOptions>();

        return services
            .AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    private static string ResolveSectionName<TOptions>() where TOptions : class
    {
        var field = typeof(TOptions).GetField(
            "SectionName",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        if (field?.GetRawConstantValue() is string fromConst)
        {
            return fromConst;
        }

        var prop = typeof(TOptions).GetProperty(
            "SectionName",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        if (prop?.GetValue(null) is string fromProp)
        {
            return fromProp;
        }

        return typeof(TOptions).Name.Replace("Options", string.Empty, StringComparison.Ordinal);
    }
}
