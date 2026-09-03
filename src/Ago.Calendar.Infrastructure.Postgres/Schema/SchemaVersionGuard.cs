using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ago.Calendar.Infrastructure.Postgres.Schema;

/// <summary>
/// `20-21`: <b>this is how ordering is enforced</b> for AGO Calendar, ported unchanged in shape from
/// <c>Ago.Chat.Infrastructure.Postgres.Schema.SchemaVersionGuard</c> (`8-08`/`adr/0056`). The failure
/// it guards against is quiet: a host rolled forward against a database still on the previous
/// migration does not crash - it runs, and fails later on whichever column it happens to touch first,
/// at request time, for whoever sent that request. On a rolling deploy that is a window where some
/// pods are lying about being healthy.
///
/// <para><b>Refusing means exiting, not failing readiness.</b> Both stop traffic reaching the pod, and
/// an unready pod is the gentler of the two - rejected anyway, for the reason `8-08` records: an
/// unready pod with no logs of its own is the same <i>shape</i> of failure as the incident it guards
/// against, whereas a container that exits with this exception's message in its logs names its own
/// cause. <c>CrashLoopBackOff</c> is a worse-looking state, and looking worse is the feature.</para>
///
/// <para><b>Why now, before `#312` deploys anything.</b> Both gaps `20-21` closes are invisible while
/// nothing is deployed and become real the moment something is - closing them first is what makes the
/// first deploy safe rather than merely possible.</para>
/// </summary>
public static class SchemaVersionGuard
{
    /// <summary>
    /// Polls until the schema is current, or throws <see cref="SchemaOutOfDateException"/> once
    /// <see cref="SchemaGuardOptions.WaitTimeout"/> has elapsed.
    ///
    /// <para>Takes <paramref name="inspect"/> as a delegate rather than a
    /// <see cref="SchemaVersionCheck"/>, for the same reason `8-08` gives: it makes the
    /// wait-then-refuse behaviour testable without a database at all. The interesting cases - pending
    /// on the first look and current on the third, still pending when the clock runs out - are about
    /// this loop, not about Postgres, and driving them through a real migration would be slower and
    /// would prove less.</para>
    /// </summary>
    public static async Task<SchemaStatus> EnsureCurrentAsync(
        Func<CancellationToken, Task<SchemaStatus>> inspect,
        SchemaGuardOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var status = await inspect(cancellationToken);
        var waited = false;

        while (!status.IsCurrent && started.Elapsed < options.WaitTimeout)
        {
            if (!waited)
            {
                waited = true;
                logger.LogWarning(
                    "Schema is behind this build: {PendingCount} migration(s) pending ({Pending}). "
                    + "Waiting up to {WaitTimeout}s for Ago.Calendar.Migrator to apply them.",
                    status.Pending.Count, string.Join(", ", status.Pending), options.WaitTimeout.TotalSeconds);
            }

            await Task.Delay(options.PollInterval, cancellationToken);
            status = await inspect(cancellationToken);
        }

        if (!status.IsCurrent)
        {
            throw new SchemaOutOfDateException(status, started.Elapsed);
        }

        if (waited)
        {
            logger.LogInformation(
                "Schema reached the expected version after {Elapsed}s.", started.Elapsed.TotalSeconds);
        }

        // Logged every time, including the ordinary case - the same reasoning `8-08` gives: a
        // migration (or a check) that runs silently is the same operational problem as one that does
        // not run. A log line naming the migration this pod was built against is what makes a
        // half-finished deploy readable from one pod's logs.
        logger.LogInformation(
            "Schema is current: built against {Expected}, {AppliedCount} migration(s) applied.",
            status.ExpectedLatest ?? "(none)", status.Applied.Count);

        if (status.AheadOfThisBuild.Count > 0)
        {
            // Not an error - see SchemaStatus.AheadOfThisBuild for why a rolled-back pod meeting a
            // newer schema is the expand/contract window working as designed, not a fault.
            logger.LogInformation(
                "The database is ahead of this build by {Count} migration(s) ({Ahead}). This is expected "
                + "during a rollback; expand/contract means the columns this build reads still exist.",
                status.AheadOfThisBuild.Count, string.Join(", ", status.AheadOfThisBuild));
        }

        return status;
    }
}
