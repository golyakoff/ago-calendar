using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres.Schema;

/// <summary>
/// `20-20`: the one type in this system that changes the schema, ported unchanged in shape from
/// `Ago.Chat.Infrastructure.Postgres.Schema.SchemaMigrationApplier` (`8-08`/`adr/0056`).
///
/// <para><b>Forward only, deliberately.</b> There is no <c>Down</c>, no <c>--target</c> and no
/// rollback here, and that is a decision rather than an omission - the same one `adr/0056` records for
/// AGO Chat. EF generates <c>Down()</c> methods and this project has never executed one; a rollback
/// path nobody has tested is worse than none, because it will be believed at exactly the moment it
/// matters. A migration that turns out to be wrong is a restore, not a `Down()`.</para>
///
/// <para><b>Idempotent with no new mechanism.</b> <c>__EFMigrationsHistory</c> already records what
/// has been applied, so a second run applies nothing and reports nothing applied - which is what lets
/// the same Job run on every deploy rather than only on the deploys that happen to need it.</para>
/// </summary>
public sealed class SchemaMigrationApplier(AgoCalendarDbContext db, SchemaVersionCheck check)
{
    /// <summary>
    /// Applies every pending migration and reports the state on both sides of the call.
    ///
    /// <para>The "before" status is captured first so the caller can name what it applied rather than
    /// only that it finished - a migration that runs silently is the same operational problem as one
    /// that does not run at all.</para>
    ///
    /// <para>No transaction is opened here. EF wraps each migration in its own, and Postgres executes
    /// DDL transactionally, so a failing migration leaves the ones before it applied and itself rolled
    /// back - which is the state the history table then correctly describes.</para>
    /// </summary>
    public async Task<SchemaMigrationOutcome> ApplyAsync(CancellationToken cancellationToken)
    {
        var before = await check.InspectAsync(cancellationToken);
        if (before.IsCurrent)
        {
            return new SchemaMigrationOutcome(before, before, []);
        }

        await db.Database.MigrateAsync(cancellationToken);

        var after = await check.InspectAsync(cancellationToken);
        return new SchemaMigrationOutcome(before, after, before.Pending);
    }
}

/// <summary>What one run of <see cref="SchemaMigrationApplier.ApplyAsync"/> did.
/// <paramref name="Applied"/> is empty for the no-op second run, which is the state that proves
/// idempotency rather than assuming it.</summary>
public sealed record SchemaMigrationOutcome(
    SchemaStatus Before, SchemaStatus After, IReadOnlyList<string> Applied);
