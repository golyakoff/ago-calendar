using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using StackExchange.Redis;

namespace Ago.Calendar.Infrastructure.Redis;

public static class ServiceCollectionExtensions
{
    /// <summary>The named resilience pipeline these registrations bind, matching the platform
    /// package's own name so <c>Resilience:Redis:*</c> means the same thing in both products'
    /// configuration.</summary>
    private const string PipelineName = "Redis";

    /// <summary>
    /// Binds <see cref="IRateLimiter"/> to the platform's <see cref="RedisRateLimiter"/> - the
    /// adapter, its Lua token bucket and its fail-open behaviour all used unchanged.
    ///
    /// <para><b>Why this method exists instead of a call to <c>AddRedisCaching</c>.</b> The platform
    /// package ships one extension method that registers the cache, the rate limiter, the
    /// distributed lock <i>and</i> <c>CacheInvalidationPublisher</c>, and that last one takes an
    /// <see cref="IEventPublisher"/>. AGO Calendar has no broker wired yet - nothing in it publishes
    /// anything until `20-05` - so calling <c>AddRedisCaching</c> makes the host fail to build its
    /// service provider under .NET's Development-environment validation:
    /// <i>"Unable to resolve service for type 'Ago.Platform.Abstractions.IEventPublisher' while
    /// attempting to activate 'CacheInvalidationPublisher'"</i>. Verified by doing it, not inferred.
    /// The package's units of composition are coarser than its units of use: a product that wants the
    /// rate limiter must also accept the cache-invalidation half and therefore a message broker.
    /// That is a real finding about the abstraction, reported rather than fixed - `20-03` makes no
    /// platform commit - and the cost of working around it here is exactly the fifteen lines below,
    /// which duplicate the multiplexer factory and the pipeline construction that
    /// <c>AddRedisCaching</c> keeps private.</para>
    ///
    /// <para>Everything that is not the wiring is still the platform's: the port
    /// (<see cref="IRateLimiter"/>), the implementation (<see cref="RedisRateLimiter"/>), the
    /// resilience builder (<see cref="ResiliencePolicyBuilder"/>) and the options shape
    /// (<c>Resilience:Redis:*</c>). This is the second product to consume the port, which is what
    /// <c>vision.md</c>'s platform claim exists to produce - a package with one caller is a guess
    /// about its second.</para>
    /// </summary>
    public static IServiceCollection AddCalendarRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Lazy: the factory runs on first resolution, so a host that never takes a rate-limit check
        // never opens a connection, and a build-time provider validation never dials Redis.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var connectionString = configuration["Redis:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "Set Redis:ConnectionString - e.g. the docker-compose Redis from local-dev.md.");
            return ConnectionMultiplexer.Connect(connectionString);
        });

        // Same defaults the platform package applies to its own "Redis" pipeline, and the same
        // reasoning: resilience.md's row for Redis asks for a short timeout and a circuit breaker,
        // not a tuned number, and none of these has been measured (CLAUDE.md). Anything under
        // Resilience:Redis:* in configuration overrides them.
        services.AddResiliencePipelineOptions(PipelineName, configuration, options =>
        {
            options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromMilliseconds(200) };
            options.CircuitBreaker = new ResilienceCircuitBreakerOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
            };
        });

        services.AddSingleton<IRateLimiter>(provider =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(PipelineName);
            var builder = new ResiliencePolicyBuilder(PipelineName);

            if (options.Timeout is not null)
            {
                builder.WithTimeout(options.Timeout);
            }

            if (options.CircuitBreaker is not null)
            {
                // Any Redis failure counts toward the breaker. A rate limiter has no source of truth
                // to fall back to, so RedisRateLimiter already fails open on error - the breaker's
                // job here is to stop paying the timeout on every request once Redis is plainly
                // down, not to change the answer.
                builder.WithCircuitBreaker(options.CircuitBreaker, _ => true);
            }

            return new RedisRateLimiter(
                provider.GetRequiredService<IConnectionMultiplexer>(),
                builder.Build(),
                provider.GetRequiredService<ILogger<RedisRateLimiter>>());
        });

        return services;
    }
}
