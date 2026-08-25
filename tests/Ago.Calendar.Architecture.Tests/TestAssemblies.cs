using System.Reflection;
using Mono.Cecil;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>One assembly, two views: <see cref="Assembly"/> for NetArchTest's type-dependency
/// predicates, <see cref="AssemblyDefinition"/> (Mono.Cecil) for the metadata checks NetArchTest has
/// no predicate for (a specific member call, an assembly's full reference list).</summary>
internal sealed record ProductAssembly(string Name, Assembly Reflection, AssemblyDefinition Cecil);

/// <summary>
/// Every assembly an arch test needs, loaded once. `20-00` builds no product behaviour at all, so
/// Domain/Application/Contracts have no public type yet to anchor a <c>typeof(X).Assembly</c>
/// lookup; every assembly here is therefore loaded uniformly by its build output path instead -
/// guaranteed to exist because this project's .csproj references each one directly, which makes
/// MSBuild copy it next to this test assembly's own output. Same shape
/// <c>Ago.Chat.Architecture.Tests</c> settled on for the same reason (`0-02`).
/// </summary>
internal static class TestAssemblies
{
    public static ProductAssembly Domain { get; } = Load("Ago.Calendar.Domain");
    public static ProductAssembly Application { get; } = Load("Ago.Calendar.Application");
    public static ProductAssembly Contracts { get; } = Load("Ago.Calendar.Contracts");
    public static ProductAssembly InfrastructurePostgres { get; } = Load("Ago.Calendar.Infrastructure.Postgres");
    public static ProductAssembly Module { get; } = Load("Ago.Calendar.Module");
    public static ProductAssembly PlatformKernel { get; } = Load("Ago.Platform.Kernel");
    public static ProductAssembly PlatformHosting { get; } = Load("Ago.Platform.Hosting");

    /// <summary>Every product assembly the "time and identity only in Infrastructure" rule
    /// (adr/0011) applies to - i.e. everything except Infrastructure itself.</summary>
    public static IReadOnlyList<ProductAssembly> NonInfrastructure { get; } =
        [Domain, Application, Contracts, Module];

    /// <summary>Every product assembly, for rules with no layer exception (the CancellationToken
    /// rule, the Ago.Chat.* isolation rule).</summary>
    public static IReadOnlyList<ProductAssembly> AllProduct { get; } =
        [Domain, Application, Contracts, InfrastructurePostgres, Module];

    /// <summary>An arch-test fixture assembly, loaded the same way but deliberately outside
    /// <see cref="AllProduct"/> - a fixture that broke the real rules for everyone would defeat the
    /// point of having them.</summary>
    public static AssemblyDefinition LoadFixture(string simpleName) =>
        AssemblyDefinition.ReadAssembly(Path.Combine(AppContext.BaseDirectory, $"{simpleName}.dll"));

    private static ProductAssembly Load(string simpleName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{simpleName}.dll");
        var reflection = Assembly.LoadFrom(path);
        var cecil = AssemblyDefinition.ReadAssembly(path);
        return new ProductAssembly(simpleName, reflection, cecil);
    }
}
