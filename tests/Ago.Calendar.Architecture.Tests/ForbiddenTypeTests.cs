using System.Reflection;
using NetArchTest.Rules;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// clean-architecture.md: Domain and Application see ports, never the concrete technology behind
/// them - that indirection is what makes an adapter swappable and a handler testable without a
/// real database, broker or cache (adr/0004, adr/0009).
/// </summary>
public class ForbiddenTypeTests
{
    private static readonly string[] ForbiddenTypes =
    [
        "Microsoft.EntityFrameworkCore.DbContext",
        "System.Data.IDbConnection",
        "RabbitMQ.Client.IConnection",
        "StackExchange.Redis.IConnectionMultiplexer",
        "Amazon.S3.IAmazonS3",
        "System.Net.Http.HttpClient",
    ];

    [Fact]
    public void Domain_ReferencesNoInfrastructureType() =>
        AssertNoForbiddenType(TestAssemblies.Domain.Reflection, "Ago.Calendar.Domain");

    [Fact]
    public void Application_ReferencesNoInfrastructureType() =>
        AssertNoForbiddenType(TestAssemblies.Application.Reflection, "Ago.Calendar.Application");

    private static void AssertNoForbiddenType(Assembly assembly, string assemblyName) =>
        Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOnAny(ForbiddenTypes)
            .GetResult()
            .ShouldPass($"{assemblyName} must see only ports, never a concrete infrastructure type");
}
