using NetArchTest.Rules;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// `20-20`: only <c>Ago.Calendar.Migrator</c> may apply a schema migration, ported from
/// <c>Ago.Chat.Architecture.Tests.SchemaMigrationTests</c> (`8-08`) - the same argument, the same
/// mechanical enforcement, one product later.
///
/// <para>Deliberately narrower than the file it is ported from: ago-chat's version also asserts
/// <c>EveryServingHost_RunsTheSchemaGuard</c>, which requires a
/// <c>SchemaGuardHostExtensions.EnsureSchemaIsCurrentAsync</c> call in every serving host. This
/// repository has no such guard yet - `20-20`'s own brief says explicitly not to build one, since a
/// host refusing to start against a stale schema is a second decision this build-and-migrate change
/// should not smuggle in. That gap is real and is called out in this item's report, not hidden by
/// pretending the guard exists.</para>
/// </summary>
public class SchemaMigrationTests
{
    private const string MigrateExtensions =
        "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";

    private const string ApplierType =
        "Ago.Calendar.Infrastructure.Postgres.Schema.SchemaMigrationApplier";

    /// <summary>
    /// The rule itself, read out of IL rather than from a type-dependency graph: <c>Migrate</c> and
    /// <c>MigrateAsync</c> are extension methods on <c>DatabaseFacade</c>, which every host
    /// legitimately touches (health checks, <c>GetAppliedMigrations</c>), so banning the *type* would
    /// ban the read half too.
    /// </summary>
    [Theory]
    [InlineData("Migrate")]
    [InlineData("MigrateAsync")]
    public void ServingHosts_NeverApplyMigrations(string member)
    {
        foreach (var host in TestAssemblies.ServingHosts)
        {
            var offenders = IlMemberScanner.FindCallers(host.Cecil, MigrateExtensions, member);

            Assert.True(offenders.Count == 0,
                $"{host.Name} calls {member}() - only Ago.Calendar.Migrator may apply a migration "
                + $"(adr/0056). Callers: {string.Join(", ", offenders)}");
        }
    }

    /// <summary>
    /// The same rule one level up, and the reason <c>SchemaMigrationApplier</c> is a separate type
    /// from <c>SchemaVersionCheck</c> at all: "does this host reference the applier" is a question a
    /// reviewer can answer from the using directives, where "does it call MigrateAsync somewhere" is
    /// not. A host that wrapped the call in a helper would slip past the IL rule above and fail here.
    /// </summary>
    [Fact]
    public void ServingHosts_NeverReferenceTheApplier()
    {
        foreach (var host in TestAssemblies.ServingHosts)
        {
            Types.InAssembly(host.Reflection)
                .Should()
                .NotHaveDependencyOn(ApplierType)
                .GetResult()
                .ShouldPass($"{host.Name} must not reference SchemaMigrationApplier - applying a "
                    + "migration is Ago.Calendar.Migrator's job and nothing else's (adr/0056)");
        }
    }

    /// <summary>
    /// The positive half: the capability exists somewhere. Without this, deleting the applier
    /// outright would make every rule above pass, which is the classic way a ban outlives the thing it
    /// was protecting.
    /// </summary>
    [Fact]
    public void TheMigrator_DoesApplyMigrations()
    {
        var callers = IlMemberScanner.FindCallers(
            TestAssemblies.InfrastructurePostgres.Cecil, MigrateExtensions, "MigrateAsync");

        Assert.True(callers.Count > 0,
            "Nothing applies migrations any more. SchemaMigrationApplier is what "
            + "Ago.Calendar.Migrator depends on; if it moved, move this rule with it.");

        Types.InAssembly(TestAssemblies.Migrator.Reflection)
            .That().HaveNameEndingWith("Runner")
            .Should()
            .HaveDependencyOn(ApplierType)
            .GetResult()
            .ShouldPass("Ago.Calendar.Migrator must be the host that applies migrations");
    }

    /// <summary>
    /// `adr/0056`: "It references <c>Ago.Calendar.Infrastructure.Postgres</c> and nothing above it."
    /// Stated in the csproj as a comment, and here as a fact. <c>Ago.Calendar.Module</c> is the one
    /// that matters: it wires Redis and validates its options at startup, so a migrator built on it
    /// could not run against a database while Redis was down - and an environment mid-incident is
    /// exactly where somebody needs to apply a migration.
    /// </summary>
    [Fact]
    public void TheMigrator_ReferencesPersistenceAndNothingAboveIt()
    {
        string[] forbidden = ["Ago.Calendar.Module", "Ago.Calendar.Api", "Ago.Calendar.Worker"];

        var offenders = TestAssemblies.Migrator.Cecil.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => forbidden.Contains(name))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Ago.Calendar.Migrator references {string.Join(", ", offenders)} - adr/0056 confines it "
            + "to Ago.Calendar.Infrastructure.Postgres and below.");
    }
}
