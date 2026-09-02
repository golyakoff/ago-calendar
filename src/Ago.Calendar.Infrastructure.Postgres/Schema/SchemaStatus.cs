namespace Ago.Calendar.Infrastructure.Postgres.Schema;

/// <summary>
/// `20-20`/`adr/0056`: what the database says about itself, compared against the migrations the
/// *calling assembly was compiled with*. That comparison is the whole mechanism, and it is why
/// nothing in this system has to state a schema version number anywhere - the same property
/// `Ago.Chat.Migrator` (`8-08`) established first; this is the second product proving it generalises.
///
/// <para><see cref="Pending"/> is the answer to "am I behind": migrations this build knows about that
/// the database has not applied. <see cref="Applied"/> is what <c>__EFMigrationsHistory</c> holds.
/// The two lists together also make "the database is <em>ahead</em> of me" visible - see
/// <see cref="AheadOfThisBuild"/>, which is deliberately not treated as an error, for the same
/// rollback-safety reason `adr/0056` gives.</para>
/// </summary>
/// <param name="Applied">Every migration id in <c>__EFMigrationsHistory</c>, oldest first.</param>
/// <param name="Pending">Migrations compiled into this build that the database has not applied,
/// oldest first. Empty means the schema is at least as new as this build expects.</param>
/// <param name="Known">Every migration id compiled into this build, oldest first.</param>
public sealed record SchemaStatus(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Known)
{
    /// <summary>The schema is at or beyond what this build needs. Zero exit code from the migrator's
    /// own Verify mode.</summary>
    public bool IsCurrent => Pending.Count == 0;

    /// <summary>
    /// Migrations the database has applied that this build has never heard of.
    ///
    /// <para><b>Reported, never fatal</b> - the same third open question `adr/0056` answers for
    /// AGO Chat. A pod rolled back to an older image against a newer schema is the expand/contract
    /// window that ADR adopts on purpose: the old code selects columns that still exist, because an
    /// expand migration only added. This type has no host-side guard consuming it yet (`20-20`
    /// deliberately does not build one - see that item's report), so today nothing reads this field at
    /// runtime; it exists because <see cref="SchemaVersionCheck"/> is the one place "ahead" can be
    /// computed at all, and a future guard should not have to re-derive it.</para>
    /// </summary>
    public IReadOnlyList<string> AheadOfThisBuild =>
        [.. Applied.Where(id => !Known.Contains(id))];

    /// <summary>The newest migration this build carries, or <see langword="null"/> for a build with
    /// none. This is "the version I expect", derived rather than configured.</summary>
    public string? ExpectedLatest => Known.Count == 0 ? null : Known[^1];
}
