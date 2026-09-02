using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres.Schema;

/// <summary>
/// `20-20`: reads the schema's state and changes nothing. Deliberately a separate type from
/// <see cref="SchemaMigrationApplier"/> rather than two methods on one class, mirroring
/// `Ago.Chat.Infrastructure.Postgres.Schema.SchemaVersionCheck` (`8-08`) exactly: a future
/// arch rule ("only Ago.Calendar.Migrator may apply a migration") can then be stated as "does this
/// host reference the applier", a question answerable from the using directives, where "does it call
/// MigrateAsync somewhere" is not. <c>SchemaMigrationTests</c> in this repository's own
/// Architecture.Tests is that rule, ported the same way.
///
/// <para>Both this type and <see cref="SchemaMigrationApplier"/> live in
/// <c>Infrastructure.Postgres</c>, not in the migrator host: EF Core's migrations API is a
/// persistence concern (clean-architecture.md), and a type in <c>Ago.Calendar.Migrator</c> could not
/// be referenced from anywhere else - hosts do not reference each other.</para>
/// </summary>
public sealed class SchemaVersionCheck(AgoCalendarDbContext db)
{
    /// <summary>
    /// Compares <c>__EFMigrationsHistory</c> against the migrations compiled into
    /// <c>Ago.Calendar.Infrastructure.Postgres</c>.
    ///
    /// <para>Reads only. <c>GetAppliedMigrations</c> issues a <c>SELECT</c> against
    /// <c>__EFMigrationsHistory</c>; <c>GetPendingMigrations</c> is that set subtracted from the
    /// assembly's own. A database with no history table at all reports every migration as pending,
    /// which is the correct answer for an empty database and the reason a first deploy needs no
    /// special case.</para>
    /// </summary>
    public async Task<SchemaStatus> InspectAsync(CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var known = db.Database.GetMigrations().ToList();

        return new SchemaStatus(applied, pending, known);
    }
}
