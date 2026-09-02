using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Ago.Calendar.Infrastructure.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Migrator;

/// <summary>What one invocation was asked to do.</summary>
public enum MigratorMode
{
    /// <summary>Apply every pending migration. The default, and the only mode that writes.</summary>
    Apply,

    /// <summary>Report whether the schema is current and change nothing. Exists so the same image can
    /// answer "is this database ready" in a script or a smoke test without being the thing that makes
    /// it ready.</summary>
    Verify,
}

/// <summary>
/// `20-20`: the migrator's whole behaviour, separated from <c>Program.cs</c> so it can be driven from
/// a test against a real Postgres - the same split `Ago.Chat.Migrator.MigratorRunner` (`8-08`) uses,
/// ported here as this repository's own equivalent deployable (`adr/0056`: "Ago.Calendar gets
/// Ago.Calendar.Migrator when it needs one").
///
/// <para><b>Exit codes are the contract</b>, so they are named here rather than left as bare integers
/// at the call sites: <see cref="Success"/> when the schema is at the version this build expects
/// (whether or not anything was applied), <see cref="Failure"/> when it is not. `adr/0056` requires a
/// non-zero exit to stop a deploy rather than be retried into a crash loop - the manifest that carries
/// <c>backoffLimit: 0</c> is the next wave's own work (this item's brief scopes deploying to a later
/// change), and this exit-code contract is what it will rely on.</para>
///
/// <para><b>No dependency-injection container at all.</b> Two objects and a
/// <c>DbContextOptionsBuilder</c> is the whole graph, and a container would add a startup surface (and
/// a set of options to validate) to a process whose value is that it does one thing and stops.</para>
///
/// <para><b>Deliberately no connectivity wait.</b> <c>Ago.Chat.Migrator</c> gained
/// <c>DatabaseAvailabilityWait</c> in `8-10`, after a live deploy started this Job racing a restarting
/// Postgres pod. Nothing has deployed AGO Calendar yet, so that incident has no analogue here to fix,
/// and porting `8-10`'s SQLSTATE/socket-error classification pre-emptively would be a second decision
/// smuggled into a build-and-migrate change. A connection failure here is still reported, just less
/// precisely: it falls into the same <c>catch</c> below as a migration failure and is logged as
/// <c>MIGRATION FAILED</c> even when no migration was attempted. Worth porting before this repository
/// is actually rolled out against a multi-workload deploy - noted in this item's report rather than
/// solved here.</para>
/// </summary>
public static class MigratorRunner
{
    public const int Success = 0;
    public const int Failure = 1;

    public static async Task<int> RunAsync(
        string connectionString,
        MigratorMode mode,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AgoCalendarDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AgoCalendarDbContext(options);

        var check = new SchemaVersionCheck(db);

        try
        {
            return mode == MigratorMode.Verify
                ? await VerifyAsync(check, output, cancellationToken)
                : await ApplyAsync(db, check, output, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Caught and reported rather than allowed to escape as an unhandled exception: the exit
            // code is the deliverable, and an unhandled exception in .NET exits with a platform-
            // dependent code that is not this contract's Failure. The message still goes out in full,
            // because the operator reading `kubectl logs` on a failed Job needs the provider's own
            // error, not a summary of it. This label also covers "could not connect at all" - see the
            // "Deliberately no connectivity wait" remark on this type.
            await output.WriteLineAsync($"MIGRATION FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                await output.WriteLineAsync($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return Failure;
        }
    }

    private static async Task<int> ApplyAsync(
        AgoCalendarDbContext db, SchemaVersionCheck check, TextWriter output, CancellationToken cancellationToken)
    {
        var applier = new SchemaMigrationApplier(db, check);
        var outcome = await applier.ApplyAsync(cancellationToken);

        if (outcome.Applied.Count == 0)
        {
            // The idempotent case, and the common one - the Job is meant to run on every deploy, not
            // only the deploys that need it, because a conditional step is a step that gets skipped
            // (the 2026-08-25 ago-chat incident this whole pattern exists to avoid repeating here).
            await output.WriteLineAsync(
                $"Schema already current at '{outcome.After.ExpectedLatest}'; {outcome.After.Applied.Count} "
                + "migration(s) applied previously, nothing to do.");
            return Success;
        }

        await output.WriteLineAsync($"Applied {outcome.Applied.Count} migration(s):");
        foreach (var migration in outcome.Applied)
        {
            await output.WriteLineAsync($"  + {migration}");
        }

        await output.WriteLineAsync($"Schema is now at '{outcome.After.ExpectedLatest}'.");
        return outcome.After.IsCurrent ? Success : Failure;
    }

    private static async Task<int> VerifyAsync(
        SchemaVersionCheck check, TextWriter output, CancellationToken cancellationToken)
    {
        var status = await check.InspectAsync(cancellationToken);
        if (status.IsCurrent)
        {
            await output.WriteLineAsync($"Schema is current at '{status.ExpectedLatest}'.");
            return Success;
        }

        await output.WriteLineAsync(
            $"Schema is behind: {status.Pending.Count} migration(s) pending against a build that expects "
            + $"'{status.ExpectedLatest}':");
        foreach (var migration in status.Pending)
        {
            await output.WriteLineAsync($"  ! {migration}");
        }

        return Failure;
    }
}
