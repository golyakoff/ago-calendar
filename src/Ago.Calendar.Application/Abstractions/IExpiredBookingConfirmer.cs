using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The confirmation sweep's one transactional step: claim a batch of expired
/// <see cref="EventStatus.PendingConfirmation"/> bookings for one tenant, confirm each, stage a
/// <c>BookingConfirmed</c> outbox row for each, and commit - all in one transaction.
///
/// <para><b>The same shape as two mechanisms this codebase already ships, and stating that is the
/// point.</b> AGO Chat's <c>ConversationAssignmentJob</c> (`4-02`) and <c>OutboxDispatcher</c>
/// (`2-04`) are both a <c>PeriodicTimer</c> around a
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c> batch claim inside one transaction per batch. Every
/// property that makes those two correct makes this one correct, for the same reasons:</para>
/// <list type="bullet">
///   <item><b><c>SKIP LOCKED</c> is what makes two replicas safe without coordinating.</b> Two
///   sweepers claiming at the same instant do not race for one row and do not block on each other -
///   Postgres hands each of them a different subset of the unlocked rows. There is no lease, no
///   advisory lock and no leader election, because the database already arbitrates.</item>
///   <item><b>A row that cannot be claimed this tick is claimed next tick.</b> The lock a claim takes
///   is released when its transaction ends, whether or not anything was confirmed, so a row skipped
///   because somebody else held it needs no explicit un-claim. A lost race is a normal outcome, never
///   an error and never logged as one.</item>
///   <item><b>The batch bound is what keeps one transaction short.</b> A sweep that claimed every
///   expired row of a busy tenant would hold locks for as long as it took to confirm them all.</item>
/// </list>
///
/// <para><b>Claim and transition commit together, and that is not a nicety.</b> `4-02` established
/// the reasoning for AGO Chat's capacity claim - a crash between claiming a row and doing the thing
/// the claim entitles you to leaves a state nobody can account for. Here it would be worse than a
/// leak: the row would stay <see cref="EventStatus.PendingConfirmation"/> past its deadline while a
/// customer has already been told they are booked, and the next tick would simply find it again and
/// confirm it - so the *visible* damage is bounded, but the outbox row would be missing for the one
/// tick that crashed after staging it separately. One transaction removes the window entirely
/// (CLAUDE.md rule 4: the state change and its integration event are committed together, published
/// separately).</para>
///
/// <para><b>Why the transaction lives behind a port rather than in the job.</b> ago-chat put exactly
/// this loop in its Worker host, because that host already had raw SQL
/// (<c>OutboxDispatcher</c>) and an <c>NpgsqlDataSource</c> in its container. This repository's
/// Worker has neither and `20-02` deliberately kept it SQL-free, so the same *shape* lands in a
/// different *file* - the adapter, where every other statement in this product already lives. The
/// alternative, a general unit-of-work the job drives, is the one
/// <see cref="ITenantRepository"/> already argued against: a transaction boundary nobody can
/// see.</para>
/// </summary>
public interface IExpiredBookingConfirmer
{
    /// <summary>
    /// Confirms up to <paramref name="batchSize"/> bookings whose
    /// <see cref="Event.ConfirmationDeadline"/> is at or before <paramref name="now"/>.
    ///
    /// <para><paramref name="now"/> is a parameter and not a clock this adapter reads, because it is
    /// the single value that decides an outcome in this product: everything else here is a
    /// compare-and-set against stored state, and this is a comparison against time
    /// (CLAUDE.md rule 11, adr/0011). A test has to be able to stand one second either side of a
    /// deadline, and it cannot if the adapter looks at a wall clock.</para>
    ///
    /// <para>Returns how many were confirmed - zero on a quiet tick, and zero is not a failure.</para>
    /// </summary>
    Task<int> ConfirmExpiredAsync(
        TenantId tenantId, DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// How many of this tenant's bookings are past their deadline and still
    /// <see cref="EventStatus.PendingConfirmation"/>.
    ///
    /// <para><b>This is the number that says the sweep is not working, and it exists because the
    /// failure is otherwise silent.</b> Doing nothing confirms a booking - so a sweep that stops
    /// running does not fail loudly, it quietly converts every pending booking into one that never
    /// confirms, while the customer has already been told they are booked. A liveness check on the
    /// job would not catch a job that runs and throws; this counts the *outcome*, so it climbs
    /// whether the loop died, the query broke, or the transaction never committed.</para>
    ///
    /// <para>Read outside the claim's transaction, after it: it is a measurement, not a decision, and
    /// nothing branches on it.</para>
    /// </summary>
    Task<int> CountOverdueAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);
}
