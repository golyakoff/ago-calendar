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
/// <para><b>No dependency-injection container</b>, exactly as <c>MigratorRunner</c>'s own remarks
/// argue for a process whose value is that it does one thing and stops. Two objects
/// (<see cref="AgoCalendarDbContext"/>, <see cref="RegisterTenantHandler"/>) and two leaf
/// implementations from the platform packages (<see cref="UuidV7Generator"/>, <see cref="SystemClock"/>)
/// are the whole graph.</para>
///
/// <para><b>`22-05`/`adr/0093`: writes only a tenant now, nothing else.</b> Before this item this tool
/// also wrote an <c>Operator</c> row - the account owner, invited by email
/// (<c>RegisterTenant.OwnerEmail</c>) and linked on their first sign-in (`adr/0088`'s mechanism). There
/// is no local <c>operators</c> table left to write that row into: the account owner's calendar
/// permissions arrive through the projection <c>RoleAssignmentsChanged</c> replicates, published by
/// whichever `ago-chat` handler created that person's account-side operator row. This tool's whole job
/// is now what its name says - registering a tenant, nothing riding along with it.</para>
///
/// <para><b>`22-03`/adr/0093: still accepts a tenant id.</b> <see cref="Program"/> reads it from
/// <c>AGO_CALENDAR_TENANT_ID</c>, optional - the account side's own id, when this run is provisioning
/// AGO Calendar for an account that already exists on the chat side, rather than the calendar minting
/// one of its own. Accepting it is not the same as trusting it: this tool has no way to confirm the
/// value names a real account, so the only protection is <see cref="IsUniqueViolation"/> below.</para>
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

        var handler = new RegisterTenantHandler(new TenantRepository(db), new UuidV7Generator(), new SystemClock());

        Result<RegisteredTenant> result;
        try
        {
            result = await handler.HandleAsync(command, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // `tenants_pkey` (the tenant id, `22-03`) or `ux_tenants_public_key` refusing a second
            // write with this run's own values - the one failure mode worth naming specifically,
            // because "already provisioned" and "something else broke" call for different reactions
            // from whoever is reading this output.
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
            $"Registered tenant {registered.TenantId.Value} (public key '{registered.PublicKey.Value}'). "
            + "No operator was created - grant this tenant's account owner a calendar permission "
            + "(e.g. calendar:configure) on the account side, and it reaches this tenant automatically "
            + "through the role-assignment projection (adr/0093).");
        return Success;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
