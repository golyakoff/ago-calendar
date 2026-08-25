namespace Ago.Calendar.ArchFixture.Compliant;

/// <summary>
/// The same use of the same idea, kept on Calendar's own side of `adr/0027`'s line: the concept it
/// needs is its own, so no reference leaves this product.
/// </summary>
public sealed class CalendarSideConcept
{
    public string Describe(string ownConcept) => ownConcept;
}
