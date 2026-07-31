using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Extensions;

/// <summary>Registers bound options and their validator with startup validation.</summary>
public static class ValidatedOptionsExtensions
{
    public static IServiceCollection AddValidatedOptions<TOptions, TValidator>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
        return services;
    }
}
