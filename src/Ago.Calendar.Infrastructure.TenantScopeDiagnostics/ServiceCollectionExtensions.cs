using Ago.Calendar.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Infrastructure.TenantScopeDiagnostics;

/// <summary>`24-17`. `Ago.Calendar.Api`'s `Program.cs` calls this by name and never sees Mono.Cecil -
/// the identical shape `ago-chat`'s own `AddTenantScopeDiagnostics` establishes.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTenantScopeDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<ITenantScopeInspector, CalendarTenantScopeInspector>();
        return services;
    }
}
