using Ago.Calendar.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Infrastructure.Time;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="IWallClockResolver"/> to the host's own tz database. One method, named after
    /// the technology it chooses, matching <c>AddCalendarPostgresPersistence</c>'s shape - the host
    /// stays the only place a concrete adapter is named (clean-architecture.md).
    ///
    /// <para>Singleton, unlike the repositories: <see cref="SystemWallClockResolver"/> holds a cache
    /// of resolved zones and no per-request state, so a scoped registration would throw that cache
    /// away on every unit of work and re-read the tz database for nothing.</para>
    /// </summary>
    public static IServiceCollection AddCalendarTimeZoneResolution(this IServiceCollection services)
    {
        services.AddSingleton<IWallClockResolver, SystemWallClockResolver>();
        return services;
    }
}
