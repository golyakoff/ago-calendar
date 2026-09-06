namespace Ago.Calendar.Worker;

/// <summary>Bound from <c>ContactPhoneRevealPruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ContactPhoneRevealPruneJobOptions
{
    public const string SectionName = "ContactPhoneRevealPruneJob";

    /// <summary>`23-12`'s own Scope: "its own retention" - a deliberate, unmeasured choice, matching
    /// `ago-chat`'s own <c>ContactRevealPruneJobOptions.RetentionWindow</c> exactly (365 days) for the
    /// identical reasoning that option's own remarks give: a reveal record is evidence of an ordinary,
    /// lawful read of one field this tenant's own rung chose to mask, not proof of a lawful basis or
    /// a completed erasure - long enough that "did anyone reveal this in the past year" still has an
    /// answer, short enough that this table does not become an indefinite personal-data store about
    /// an operator.</summary>
    public TimeSpan RetentionWindow { get; set; } = TimeSpan.FromDays(365);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 1000;

    public int MaxBatchesPerCycle { get; set; } = 50;
}
