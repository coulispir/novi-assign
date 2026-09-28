using System;

using Microsoft.Extensions.Configuration;

namespace App.Host.Infrastructure;

public static class ConfigurationExtensions
{
    /// <summary>
    /// Returns the named connection string, or throws when it is missing. Connection strings are consumed while services
    /// are being registered (EF Core, the Quartz job store, Redis), before options validation can run, so they are
    /// checked here instead and the host fails before it starts.
    /// </summary>
    public static string GetRequiredConnectionString(this IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string '{name}' is missing from configuration. Set 'ConnectionStrings:{name}' (environment variable 'ConnectionStrings__{name}').");
        }

        return connectionString;
    }
}
