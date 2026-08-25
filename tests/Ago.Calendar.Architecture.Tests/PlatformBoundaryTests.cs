using System.Reflection;
using NetArchTest.Rules;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// adr/0012: the platform never references a product. The package boundary already makes this true
/// by construction - <c>ago-platform</c>'s source tree has no access to <c>ago-calendar</c>'s - but
/// the test states it anyway, so the guarantee is a checked fact rather than something that only
/// holds until someone points the dev override (<c>AgoCalendarDevOverride</c>) the wrong way.
///
/// <para>The assertion is deliberately the wider "no <c>Ago</c> product at all", not just "no
/// Ago.Calendar": a platform package that had grown a reference to <c>Ago.Chat.*</c> would be just
/// as broken from here, and this repository is the second place able to notice it.</para>
/// </summary>
public class PlatformBoundaryTests
{
    [Fact]
    public void Kernel_NeverReferencesAnyProductAssembly() =>
        AssertNoProductDependency(TestAssemblies.PlatformKernel, "Ago.Platform.Kernel");

    [Fact]
    public void Hosting_NeverReferencesAnyProductAssembly() =>
        AssertNoProductDependency(TestAssemblies.PlatformHosting, "Ago.Platform.Hosting");

    private static void AssertNoProductDependency(ProductAssembly assembly, string assemblyName)
    {
        AssertNoTypeDependency(assembly.Reflection, assemblyName, "Ago.Calendar");
        AssertNoTypeDependency(assembly.Reflection, assemblyName, "Ago.Chat");

        var offenders = assembly.Cecil.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => name.StartsWith("Ago.Calendar.", StringComparison.Ordinal)
                || name.StartsWith("Ago.Chat.", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{assemblyName} references a product assembly: {string.Join(", ", offenders)}");
    }

    private static void AssertNoTypeDependency(Assembly assembly, string assemblyName, string forbiddenNamespace) =>
        Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOn(forbiddenNamespace)
            .GetResult()
            .ShouldPass($"{assemblyName} must never reference a {forbiddenNamespace}.* assembly");
}
