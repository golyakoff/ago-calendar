using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Time;
using Ago.Platform.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Module;

/// <summary>
/// The one <see cref="IProductModule"/> every AGO Calendar host loads
/// (docs/architecture/clean-architecture.md), and the second implementation of that interface in
/// existence - which is the point of `20-00`: the platform's hosting seam had exactly one consumer
/// until now, and an abstraction with one caller is a guess about the second.
///
/// <para>`20-02` is the item that gave it something to register. Both adapters and all three
/// handlers are wired here rather than in each host's <c>Program.cs</c>, because the composition is
/// identical for the API and the Worker - the hosts differ in what they *run*, never in what the
/// product is (adr/0013). What stays in a host is only what is genuinely host-shaped: the
/// materialisation job's own options and its <c>AddHostedService</c> registration, which belong to
/// the one host that runs it.</para>
/// </summary>
public sealed class CalendarModule : IProductModule
{
    public string Name => "Ago.Calendar";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // From the environment, never from a checked-in settings file. This repository is public
        // from its first commit, and a connection string in appsettings.json is how a credential
        // ends up in git history - the same call ChatModule made for AGO Chat.
        var connectionString = Environment.GetEnvironmentVariable("AGO_CALENDAR_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set AGO_CALENDAR_CONNECTION_STRING - e.g. the docker-compose Postgres from local-dev.md.");

        services.AddCalendarPostgresPersistence(connectionString);

        // The single wall-clock-to-instant bridge (adr/0049). Registered for every host, not only
        // the Worker: the manual-edit handlers convert too, and they are the API's business.
        services.AddCalendarTimeZoneResolution();

        services.AddScoped<MaterializeAvailabilityHandler>();
        services.AddScoped<DeleteDayOffHandler>();
        services.AddScoped<EditDayBoundaryHandler>();
    }
}
