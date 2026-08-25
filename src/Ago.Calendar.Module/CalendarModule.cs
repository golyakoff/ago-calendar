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
/// <para>It registers nothing until `20-01` has a use case, an endpoint or a consumer to wire up. A
/// module that registers nothing yet is the honest state of a skeleton, not a bug.</para>
/// </summary>
public sealed class CalendarModule : IProductModule
{
    public string Name => "Ago.Calendar";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}
