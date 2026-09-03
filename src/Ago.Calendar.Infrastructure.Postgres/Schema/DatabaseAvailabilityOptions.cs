namespace Ago.Calendar.Infrastructure.Postgres.Schema;

/// <summary>
/// `20-21`: how long <see cref="DatabaseAvailabilityWait"/> treats "Postgres is not accepting
/// connections" as a state to wait through rather than a reason to fail - ported unchanged in shape
/// from <c>Ago.Chat.Infrastructure.Postgres.Schema.DatabaseAvailabilityOptions</c> (`8-10`).
///
/// <para>Deliberately <b>not</b> an <c>IOptions</c>-bound class like the schema guard's own options
/// (`ago-root#339`, a separate item), for the identical reason `8-10` gives: <c>Ago.Calendar.Migrator</c> reads exactly one required
/// environment variable and builds no configuration pipeline at all (its own <c>Program.cs</c>
/// remarks); adding one here to carry two timespans would spend that property to buy nothing.
/// <c>Program.cs</c> reads one optional variable and constructs this directly.</para>
/// </summary>
public sealed class DatabaseAvailabilityOptions
{
    /// <summary>
    /// The environment variable <c>Ago.Calendar.Migrator</c> reads to override
    /// <see cref="WaitTimeout"/>. Optional: unset means <see cref="DefaultWaitTimeout"/>, so the
    /// migrator still has exactly one variable it <i>requires</i>.
    /// </summary>
    public const string WaitTimeoutVariable = "AGO_CALENDAR_DB_WAIT_TIMEOUT";

    /// <summary>
    /// <b>Ninety seconds - the same figure `8-10` measured for AGO Chat, carried over rather than
    /// re-measured.</b> Nothing has deployed AGO Calendar yet (`#312` is the first time), so there is
    /// no incident here to size this against the way `8-10` sized it against the 2026-08-26 ago-chat
    /// deploy. What transfers without re-measurement is the container-startup floor: the same
    /// <c>postgres:17-alpine</c> image, the same <see cref="PollInterval"/>, so `8-10`'s measured
    /// 4.2-4.3s figures are still the right order of magnitude for "a Postgres container coming up
    /// cold". What is <b>not</b> measured, and is stated as such (CLAUDE.md rule 7), is the harder
    /// case `8-10` sized this against - a pod restarting mid-rollout with scheduling and a re-attached
    /// volume added on top. Ninety seconds is carried over as a judgement bounded the same way `8-10`
    /// records: comfortably above the measured floor, and expected to be revisited once `#312`
    /// actually rolls this product out and produces a real number to size it against.</para>
    /// </summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long to keep re-probing before giving up. Exceeding it is a real failure that
    /// stops the deploy - the point is to tell <i>not yet</i> from <i>not going to</i>, not to wait
    /// forever.</summary>
    public TimeSpan WaitTimeout { get; init; } = DefaultWaitTimeout;

    /// <summary>
    /// How long to pause between probes. Two seconds for the same reason the schema guard's own poll
    /// interval is (`ago-root#339`): a refused connection fails in milliseconds, so without a pause
    /// this would be a spin loop against a socket, and with a much longer one the migrator would idle
    /// for seconds after Postgres was already up.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads <see cref="WaitTimeoutVariable"/>, falling back to <see cref="DefaultWaitTimeout"/> when
    /// it is absent, and refusing an unparseable value rather than silently defaulting - a typo in a
    /// manifest that quietly restored the default would be exactly the kind of drift this item exists
    /// to prevent.
    /// </summary>
    public static bool TryReadFromEnvironment(
        Func<string, string?> readVariable, out DatabaseAvailabilityOptions options, out string? error)
    {
        var raw = readVariable(WaitTimeoutVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            options = new DatabaseAvailabilityOptions();
            error = null;
            return true;
        }

        if (!TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < TimeSpan.Zero)
        {
            options = new DatabaseAvailabilityOptions();
            error = $"{WaitTimeoutVariable} is not a valid non-negative timespan (got '{raw}'). "
                + "Use hh:mm:ss - e.g. 00:01:30.";
            return false;
        }

        options = new DatabaseAvailabilityOptions { WaitTimeout = parsed };
        error = null;
        return true;
    }
}
