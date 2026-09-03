using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Provisioner;

/// <summary>
/// `ago-root#363`: the whole behaviour, separated from <c>Program.cs</c> so it can be driven from a
/// test against a real Postgres - the same split <c>Ago.Calendar.Migrator.MigratorRunner</c> uses,
/// for the same reason.
///
/// <para><b>No dependency-injection container, exactly as <c>MigratorRunner</c>'s own remarks argue
/// for a process whose value is that it does one thing and stops.</b> Three objects
/// (<see cref="AgoCalendarDbContext"/>, <see cref="TenantProvisioningStore"/>,
/// <see cref="RegisterTenantHandler"/>) and two leaf implementations from the platform packages
/// (<see cref="UuidV7Generator"/>, <see cref="SystemClock"/>) are the whole graph - the same handler
/// and the same port <c>Ago.Calendar.Api</c>'s DI container would hand out, constructed by hand
/// instead of resolved, because there is exactly one caller and it runs once.</para>
///
/// <para><b>Never accepts a Keycloak subject.</b> <see cref="RegisterTenant.ExternalSubjectId"/> is
/// always <see langword="null"/> here - <see cref="Program"/> reads no environment variable for it.
/// A real tenant's owner has not signed in yet, so there is nothing to supply; offering the parameter
/// anyway would invite the shortcut this item's report argues against (inventing or guessing a
/// subject id). <see cref="RegisterTenant.OwnerEmail"/> is the only identity this tool ever writes,
/// which is what keeps the write this tool can make narrow enough to audit by reading this file.</para>
///
/// <para><b>`22-03`/adr/0093: does accept a tenant id.</b> <see cref="Program"/> reads it from
/// <c>AGO_CALENDAR_TENANT_ID</c>, optional - the account side's own id, when this run is provisioning
/// AGO Calendar for an account that already exists on the chat side, rather than the calendar minting
/// one of its own. Accepting it is not the same as trusting it: this tool has no way to confirm the
/// value names a real account, so the only protection is <see cref="IsUniqueViolation"/> below,
/// extended by the primary key to a second run repeating the same id, exactly as it already refused a
/// second run repeating the same public key.</para>
/// </summary>
public static class ProvisionerRunner
{
    public const int Success = 0;
    public const int Failure = 1;

    public static async Task<int> RunAsync(
        string connectionString, RegisterTenant command, TextWriter output, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AgoCalendarDbContext(options);

        var handler = new RegisterTenantHandler(
            new TenantProvisioningStore(db), new UuidV7Generator(), new SystemClock());

        Result<RegisteredTenant> result;
        try
        {
            result = await handler.HandleAsync(command, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // `tenants_pkey` (the tenant id, `22-03`), `ux_tenants_public_key` or the
            // `external_subject_id`/invited-email siblings refusing a second write with this run's own
            // values - the one failure mode worth naming specifically, because "already provisioned"
            // and "something else broke" call for different reactions from whoever is reading this
            // output. The message stays generic about *which* value collided rather than naming the
            // public key specifically, because since `22-03` the id can collide too, and this tool has
            // no cheap way to tell the two apart after Postgres has already rolled the write back.
            await output.WriteLineAsync(
                "ALREADY PROVISIONED: this run's tenant id or public key collides with a row that "
                + "already exists. Nothing was written by this run. If this is a genuine repeat, that "
                + "is the protection working as intended; if it is not, choose a different id or "
                + "public key.");
            return Failure;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Caught and reported rather than left to exit with a platform-dependent code - the exit
            // code is the deliverable, the same reasoning `MigratorRunner`'s own catch gives.
            await output.WriteLineAsync($"PROVISIONING FAILED: {exception.GetType().Name}: {exception.Message}");
            if (exception.InnerException is { } inner)
            {
                await output.WriteLineAsync($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return Failure;
        }

        if (!result.IsSuccess)
        {
            await output.WriteLineAsync($"REFUSED: {result.Error!.Value.Code}: {result.Error.Value.Message}");
            return Failure;
        }

        var registered = result.Value;
        await output.WriteLineAsync(
            $"Registered tenant {registered.TenantId.Value} (public key '{registered.PublicKey.Value}') "
            + $"with account owner {registered.OperatorId.Value}.");
        await output.WriteLineAsync(
            $"The account owner is invited, not yet linked: they must sign in to the calendar console "
            + $"once with a Keycloak account whose email matches '{command.OwnerEmail}' before this "
            + "operator resolves to anything (adr/0088's own mechanism, unchanged).");
        return Success;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
