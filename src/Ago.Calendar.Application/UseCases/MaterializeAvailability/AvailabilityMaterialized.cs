namespace Ago.Calendar.Application.UseCases.MaterializeAvailability;

/// <summary>
/// What one run over one calendar actually did. Returned rather than logged inside the handler so
/// that a test can assert on it - "the second run inserted nothing" is this item's central claim,
/// and a claim only readable in a log line is a claim nobody checks.
/// </summary>
/// <param name="DaysConsidered">Days in the window, including the ones skipped.</param>
/// <param name="DaysSkipped">Days that already had at least one event row and were therefore left
/// untouched - the non-destructive rule, counted.</param>
/// <param name="SlotsInserted">Rows the database actually accepted. Lower than the number generated
/// when another replica won the same day; zero on every run after the first.</param>
public readonly record struct AvailabilityMaterialized(int DaysConsidered, int DaysSkipped, int SlotsInserted)
{
    public static AvailabilityMaterialized Nothing => new(0, 0, 0);
}
