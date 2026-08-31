using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-12`'s own migration, `Stage20AddAccountOwnerAndContactVisibility`, against data seeded to look
/// like what a real, already-provisioned deployment would have had the moment before this migration
/// ran - proving the two hand-written backfills the migration's own comments describe, not merely that
/// the columns exist. Uses <c>MigrationReversibilityTests</c>' own technique (driving
/// <see cref="IMigrator"/> directly by name) to reach the "just before this migration" state, since
/// <see cref="PostgresFixture"/> otherwise always starts from every migration already applied.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountOwnerBackfillMigrationTests(PostgresFixture fixture)
{
    private const string PreviousMigration = "20260831103632_Stage20AddCustomerPhoneVerifiedAt";

    [Fact]
    public async Task Migrating_BackfillsIsAccountOwner_ForTheEarliestOperatorPerTenant_AndGrantsCustomerReadFromExistingRolePermissions()
    {
        await using (var db = fixture.CreateDbContext())
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        // Two tenants, so the backfill's own per-tenant scoping (not a single global MIN(id)) is
        // exercised, not merely "there happens to be one tenant so any id would pass".
        var tenantA = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var tenantB = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero));

        // UUIDv7 ids minted an hour apart so their ordering is unambiguous even across a slow test
        // run - the same property the migration's own comment relies on.
        var earlyOfA = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        var laterOfA = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero));
        var onlyOfB = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));

        var roleWithRead = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        var roleWithoutRead = Guid.CreateVersion7(new DateTimeOffset(2026, 1, 1, 1, 0, 1, TimeSpan.Zero));

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await InsertTenantAsync(connection, tenantA, "key-a");
            await InsertTenantAsync(connection, tenantB, "key-b");

            await InsertOperatorAsync(connection, earlyOfA, tenantA, "Early");
            await InsertOperatorAsync(connection, laterOfA, tenantA, "Later");
            await InsertOperatorAsync(connection, onlyOfB, tenantB, "Only");

            await InsertRoleAsync(connection, roleWithRead, tenantA, "Operator", ["customer:read", "booking:reject"]);
            await InsertRoleAsync(connection, roleWithoutRead, tenantA, "Dispatcher", ["booking:reject"]);

            // The earliest operator of tenant A holds the role *without* CustomerRead, and the later
            // one holds the role *with* it - deliberately the opposite of "account owner" pairing, so
            // the two backfills are proven independently: is_account_owner follows id order alone,
            // grants_customer_read follows the role's own permissions alone.
            await InsertAssignmentAsync(connection, earlyOfA, roleWithoutRead);
            await InsertAssignmentAsync(connection, laterOfA, roleWithRead);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var reader = await fixture.DataSource.OpenConnectionAsync();

        Assert.True(await IsAccountOwnerAsync(reader, earlyOfA));
        Assert.False(await IsAccountOwnerAsync(reader, laterOfA));
        Assert.True(await IsAccountOwnerAsync(reader, onlyOfB));

        Assert.False(await GrantsCustomerReadAsync(reader, earlyOfA, roleWithoutRead));
        Assert.True(await GrantsCustomerReadAsync(reader, laterOfA, roleWithRead));
    }

    private static async Task InsertTenantAsync(NpgsqlConnection connection, Guid id, string publicKey)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO tenants (id, name, created_at, allowed_origins, public_key)
            VALUES (@id, 'Barbershop', now(), ARRAY[]::text[], @publicKey)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("publicKey", publicKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertOperatorAsync(NpgsqlConnection connection, Guid id, Guid tenantId, string name)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO operators (id, tenant_id, display_name) VALUES (@id, @tenantId, @name)", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertRoleAsync(
        NpgsqlConnection connection, Guid id, Guid tenantId, string name, string[] permissions)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO roles (id, tenant_id, name, permissions) VALUES (@id, @tenantId, @name, @permissions)",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("permissions", permissions);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAssignmentAsync(NpgsqlConnection connection, Guid operatorId, Guid roleId)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO operator_roles (operator_id, role_id) VALUES (@operatorId, @roleId)", connection);
        command.Parameters.AddWithValue("operatorId", operatorId);
        command.Parameters.AddWithValue("roleId", roleId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsAccountOwnerAsync(NpgsqlConnection connection, Guid operatorId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT is_account_owner FROM operators WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> GrantsCustomerReadAsync(NpgsqlConnection connection, Guid operatorId, Guid roleId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT grants_customer_read FROM operator_roles WHERE operator_id = @operatorId AND role_id = @roleId",
            connection);
        command.Parameters.AddWithValue("operatorId", operatorId);
        command.Parameters.AddWithValue("roleId", roleId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
