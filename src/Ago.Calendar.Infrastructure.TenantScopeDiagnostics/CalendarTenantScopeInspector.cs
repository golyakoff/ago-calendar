using Ago.Calendar.Application.Abstractions;
using Mono.Cecil;

namespace Ago.Calendar.Infrastructure.TenantScopeDiagnostics;

/// <summary>
/// `24-17`: <see cref="ITenantScopeInspector"/>'s real implementation - reads
/// `Ago.Calendar.Application.dll` next to whichever host process is running, with Mono.Cecil, the
/// same "why this can find the assembly by a bare file name" and "why this is never cached" reasoning
/// `ago-chat`'s own `TenantScopeInspector` gives for itself.
/// </summary>
public sealed class CalendarTenantScopeInspector : ITenantScopeInspector
{
    private const string ApplicationAssemblyFileName = "Ago.Calendar.Application.dll";

    private readonly string _applicationAssemblyPath;

    public CalendarTenantScopeInspector()
        : this(Path.Combine(AppContext.BaseDirectory, ApplicationAssemblyFileName))
    {
    }

    /// <summary>Internal seam for a test that wants to point this at a different assembly - the same
    /// reason `ago-chat`'s own `TenantScopeInspector` keeps one.</summary>
    internal CalendarTenantScopeInspector(string applicationAssemblyPath)
    {
        _applicationAssemblyPath = applicationAssemblyPath;
    }

    public Task<TenantScopeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Scan(_applicationAssemblyPath));
    }

    /// <summary>The computation itself, `static` so a test can call it directly against an arbitrary
    /// assembly path.</summary>
    public static TenantScopeSnapshot Scan(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var entryPoints = CalendarTenantScopeRule.Scan(assembly);

        var gated = entryPoints.Where(e => e.IsRbacGated).ToList();
        var notGated = entryPoints.Where(e => !e.IsRbacGated).Select(e => e.Key).ToList();

        var handlerClasses = entryPoints
            .Select(e => e.Key[..e.Key.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new TenantScopeSnapshot(
            EntryPoints: entryPoints.Count,
            HandlerClasses: handlerClasses,
            RbacGated: gated.Count,
            NotGated: notGated.Count,
            NotGatedKeys: notGated);
    }
}
