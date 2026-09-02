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

    /// <summary>`20-02`: the tz-database adapter, and the one assembly allowed to mention
    /// <c>TimeZoneInfo</c> - see <see cref="TimeZoneIsolationTests"/>.</summary>
    public static ProductAssembly InfrastructureTime { get; } = Load("Ago.Calendar.Infrastructure.Time");

    public static ProductAssembly Module { get; } = Load("Ago.Calendar.Module");
    public static ProductAssembly PlatformKernel { get; } = Load("Ago.Platform.Kernel");
    public static ProductAssembly PlatformHosting { get; } = Load("Ago.Platform.Hosting");

    // `20-20`: the deployables. Loaded the same way as everything else here, by simple name from
    // this project's own output directory.
    public static ProductAssembly Api { get; } = Load("Ago.Calendar.Api");
    public static ProductAssembly Worker { get; } = Load("Ago.Calendar.Worker");
    public static ProductAssembly Migrator { get; } = Load("Ago.Calendar.Migrator");

    /// <summary>The serving hosts - `adr/0013`'s split, applied here the same way it is in
    /// `Ago.Chat.Architecture.Tests`. `20-20`'s rule is precisely that neither may apply a schema
    /// migration; <see cref="Migrator"/> is deliberately not in this list, because it is the one that
    /// may. No `Ago.Calendar.Webhooks` exists in this product (`20-00`'s own scope note), so this list
    /// has two members where ago-chat's has three.</summary>
    public static IReadOnlyList<ProductAssembly> ServingHosts { get; } = [Api, Worker];

    /// <summary>Every product assembly the "time and identity only in Infrastructure" rule
    /// (adr/0011) applies to - i.e. everything except Infrastructure itself.</summary>
    public static IReadOnlyList<ProductAssembly> NonInfrastructure { get; } =
        [Domain, Application, Contracts, Module];

    /// <summary>Every product assembly, for rules with no layer exception (the CancellationToken
    /// rule, the Ago.Chat.* isolation rule).</summary>
    public static IReadOnlyList<ProductAssembly> AllProduct { get; } =
        [Domain, Application, Contracts, InfrastructurePostgres, InfrastructureTime, Module];

    /// <summary>The same list, in the order <see cref="TimeZoneIsolationTests"/> reports offenders
    /// in - an alias so that "every assembly" reads as the rule's own subject rather than as a
    /// list borrowed from another rule.</summary>
    public static IReadOnlyList<ProductAssembly> EveryProductAssembly => AllProduct;

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
