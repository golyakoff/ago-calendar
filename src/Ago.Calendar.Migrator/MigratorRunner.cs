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
/// `20-20`/`ago-root#340`: the migrator's whole behaviour, separated from <c>Program.cs</c> so it can be
/// driven from a test against a real Postgres - the same split <c>Ago.Chat.Migrator.MigratorRunner</c>
/// (`8-08`) uses.
///
/// <para><b>Exit codes are the contract</b>, so they are named here rather than left as bare integers
/// at the call sites: <see cref="Success"/> when the schema is at the version this build expects
/// (whether or not anything was applied), <see cref="Failure"/> when it is not. `adr/0056` requires a
/// non-zero exit to stop a deploy rather than be retried into a crash loop.</para>
///
/// <para><b>No dependency-injection container at all.</b> Two objects and a
/// <c>DbContextOptionsBuilder</c> is the whole graph, and a container would add a startup surface (and
/// a set of options to validate) to a process whose value is that it does one thing and stops.</para>
///
/// <para><b>`ago-root#340`: the connectivity wait, added.</b> `20-20`'s own report named this gap
/// explicitly rather than leaving it implicit: the migrator's correctness depended on
/// <c>ago-deploy</c>'s init container waiting for Postgres on its behalf, which is a dependency on
/// deployment configuration to make application code correct - exactly the coupling the platform's own
/// abstractions exist to remove. Ported unchanged in shape from <c>Ago.Chat.Migrator.MigratorRunner</c>
/// (`8-10`): the wait wraps <b>only</b> the connectivity probe, never a migration. By the time
/// <see cref="SchemaVersionCheck"/>/<see cref="SchemaMigrationApplier"/> are constructed below, this
/// wait has already returned, so a genuinely failing migration is still reported and exited on
/// immediately - never retried, never waited on.</para>
/// </summary>
public static class MigratorRunner
{
    public const int Success = 0;
    public const int Failure = 1;

    public static async Task<int> RunAsync(
        string connectionString,
        MigratorMode mode,
        TextWriter output,
        CancellationToken cancellationToken,
        DatabaseAvailabilityOptions? wait = null)
    {
        // `ago-root#340`: the wait is here, in front of everything, and it is the *only* thing it
        // wraps. The migration below is reached with a connection already proven to authenticate and
        // answer, so a failure past this point is a migration failure and is reported and exited on
        // immediately.
        var availability = await DatabaseAvailabilityWait.UntilReadyAsync(
            token => DatabaseAvailabilityWait.ProbeAsync(connectionString, token),
            wait ?? new DatabaseAvailabilityOptions(),
            output,
            cancellationToken);

        if (availability.Outcome != DatabaseAvailability.Available)
        {
            await ReportUnavailableAsync(availability, connectionString, output);
            return Failure;
        }

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
            // because the operator reading the logs on a failed Job needs the provider's own error,
            // not a summary of it.
            await output.WriteLineAsync($"MIGRATION FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                await output.WriteLineAsync($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return Failure;
        }
    }

    /// <summary>
    /// `ago-root#340`: the two failures that are <b>not</b> migration failures, and they are worded so
    /// that the first token of the first line tells them apart in the logs - ported unchanged in shape
    /// from <c>Ago.Chat.Migrator.MigratorRunner.ReportUnavailableAsync</c> (`8-10`).
    ///
    /// <para>The item's whole premise is that "gave up waiting for Postgres" and "the migration threw"
    /// need different reactions - one is an infrastructure problem, the other is a code problem.
    /// Every line below therefore says explicitly that no migration was attempted, because the
    /// operator's first question on a failed run is whether the database was left half-changed.</para>
    /// </summary>
    private static async Task ReportUnavailableAsync(
        DatabaseAvailabilityResult availability, string connectionString, TextWriter output)
    {
        var target = DatabaseAvailabilityWait.DescribeTarget(connectionString);
        var last = availability.LastFailure is null
            ? "(no error recorded)"
            : DatabaseAvailabilityWait.Describe(availability.LastFailure);

        if (availability.Outcome == DatabaseAvailability.GaveUpWaiting)
        {
            await output.WriteLineAsync(
                $"WAITING FOR DATABASE FAILED: gave up after {availability.Elapsed.TotalSeconds:F1}s and "
                + $"{availability.Attempts} attempt(s) waiting for Postgres at {target} to accept "
                + "connections.");
            await output.WriteLineAsync($"  last attempt: {last}");
            await output.WriteLineAsync(
                "  No migration was attempted and the schema is unchanged. This is an infrastructure "
                + "problem, not a migration problem: check that Postgres is running and reachable, then "
                + "re-run this Job.");
            return;
        }

        await output.WriteLineAsync(
            $"CANNOT CONNECT TO DATABASE: Postgres at {target} rejected the connection with something "
            + "waiting will not fix.");
        await output.WriteLineAsync($"  {last}");
        await output.WriteLineAsync(
            "  No migration was attempted and the schema is unchanged. Reported immediately rather than "
            + "waited on, because a wrong credential, a missing database or a missing grant does not "
            + "become correct with time.");
    }

    private static async Task<int> ApplyAsync(
        AgoCalendarDbContext db, SchemaVersionCheck check, TextWriter output, CancellationToken cancellationToken)
    {
        var applier = new SchemaMigrationApplier(db, check);
        var outcome = await applier.ApplyAsync(cancellationToken);

        if (outcome.Applied.Count == 0)
        {
            // The idempotent case, and the common one - the Job is meant to run on every deploy, not
            // only the deploys that need it, because a conditional deploy step is a step that gets
            // skipped.
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
