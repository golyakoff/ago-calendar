using NetArchTest.Rules;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// `20-20`/`20-21` (`ago-root#339`): only <c>Ago.Calendar.Migrator</c> may apply a schema migration,
/// and every serving host must run the guard - ported from
/// <c>Ago.Chat.Architecture.Tests.SchemaMigrationTests</c> (`8-08`) - the same argument, the same
/// mechanical enforcement, one product later.
///
/// <para>`20-20` left this file deliberately narrower than the one it is ported from, missing exactly
/// <see cref="EveryServingHost_RunsTheSchemaGuard"/> - its own brief said explicitly not to build a
/// guard yet, since a host refusing to start against a stale schema was a second decision that
/// build-and-migrate change should not smuggle in. `20-21` is that second decision, made
/// deliberately, and this file now matches the one it was ported from in full.</para>
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

    /// <summary>
    /// `20-21` (`ago-root#339`): every serving host must actually run the guard. The rules above stop
    /// a host from *applying* a migration; this one stops the opposite failure - a host that neither
    /// applies nor checks, which is the same quiet failure `8-08` closed for AGO Chat: a host rolled
    /// forward against a database still on the previous migration does not crash, it runs, and fails
    /// later on whichever column it happens to touch first.
    ///
    /// <para>A new host added later starts out failing this, which is the intended cost: a host that
    /// serves traffic against Postgres has to say what it does about the schema.</para>
    /// </summary>
    [Fact]
    public void EveryServingHost_RunsTheSchemaGuard()
    {
        foreach (var host in TestAssemblies.ServingHosts)
        {
            var callers = IlMemberScanner.FindCallers(
                host.Cecil,
                "Ago.Calendar.Infrastructure.Postgres.Schema.SchemaGuardHostExtensions",
                "EnsureSchemaIsCurrentAsync");

            Assert.True(callers.Count > 0,
                $"{host.Name} never calls EnsureSchemaIsCurrentAsync - it would start and serve traffic "
                + "against a schema older than the migrations it was compiled with (20-21).");
        }
    }
}
