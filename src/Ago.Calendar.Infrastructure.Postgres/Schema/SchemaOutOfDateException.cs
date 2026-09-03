namespace Ago.Calendar.Infrastructure.Postgres.Schema;

/// <summary>
/// `20-21`: thrown by <see cref="SchemaVersionGuard"/> when a host's own build carries migrations the
/// database has not applied. It is thrown <i>before</i> the host starts listening, so the process
/// exits non-zero and never serves a request against a schema it does not match - ported unchanged in
/// shape from <c>Ago.Chat.Infrastructure.Postgres.Schema.SchemaOutOfDateException</c> (`8-08`).
///
/// <para>The message names the pending migrations rather than saying "schema out of date", for the
/// same reason `8-08` gives: a message that only says "some queries fail" three layers away from the
/// cause is exactly the failure this type exists to make loud and specific instead.</para>
/// </summary>
public sealed class SchemaOutOfDateException(SchemaStatus status, TimeSpan waited)
    : Exception(BuildMessage(status, waited))
{
    public SchemaStatus Status { get; } = status;

    private static string BuildMessage(SchemaStatus status, TimeSpan waited) =>
        $"This host was built against migration '{status.ExpectedLatest}', and the database has not applied "
        + $"{status.Pending.Count} of the migrations it needs after waiting {waited.TotalSeconds:0.#}s: "
        + $"{string.Join(", ", status.Pending)}. "
        + "Run Ago.Calendar.Migrator against this database before starting this host - it is the only thing "
        + "that applies migrations (adr/0056). Refusing to start: serving traffic against an older schema "
        + "returns 200s for pages whose queries fail, which is the failure this check exists to prevent.";
}
