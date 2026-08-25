using NetArchTest.Rules;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>The dependency rule (clean-architecture.md): source-code dependencies point inwards
/// only - an inner layer knows nothing about an outer one, not the type, not the namespace, not the
/// package.</summary>
public class LayeringTests
{
    [Fact]
    public void Domain_DependsOnlyOnPlatformKernelAndTheBcl()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Ago.Calendar.Domain", "Ago.Platform.Kernel" };

        var offenders = TestAssemblies.Domain.Cecil.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => !allowed.Contains(name) && !BclAssemblyNames.IsBcl(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Ago.Calendar.Domain references assemblies outside Ago.Platform.Kernel and the BCL: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrAnyHost() =>
        Types.InAssembly(TestAssemblies.Application.Reflection)
            .Should()
            .NotHaveDependencyOnAny(
                "Ago.Calendar.Infrastructure.Postgres",
                "Ago.Calendar.Api",
                "Ago.Calendar.Worker",
                "Ago.Calendar.Module")
            .GetResult()
            .ShouldPass("Ago.Calendar.Application must not depend on Infrastructure or on any host");
}
