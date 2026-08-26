using System.Text.Json;
using Ago.Calendar.Contracts;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// The product spec's central design decision, asserted rather than described.
///
/// <para>The row a successful booking leaves behind is <c>PendingConfirmation</c> with a deadline,
/// and an operator may still veto it (`20-04`). The customer is told none of that - they are told
/// they are booked, full stop. Getting this backwards is not a cosmetic slip: a countdown on a
/// confirmation page turns "you have an appointment" into "you might have an appointment", which is
/// the experience the product exists to beat, and it would arrive as a one-line addition to a
/// response record that nobody reviewed as a product change.</para>
///
/// <para>So the guard is on the serialised form, not on the type's declaration: it catches a new
/// field, a renamed one, and a nested object equally, and it fails at the moment somebody adds the
/// field rather than at the moment a designer notices it on the page.</para>
/// </summary>
public class BookingConfirmationDisclosureTests
{
    private static readonly string[] ForbiddenSubstrings =
    [
        "pending",
        "deadline",
        "confirmationwindow",
        "expires",
        "status",
        "state",
    ];

    [Fact]
    public void TheVisitorFacingConfirmationLeaksNoPendingState()
    {
        var response = new BookingConfirmedResponse(
            BookingId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            WorkerId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            StartsAt: new DateTimeOffset(2026, 5, 4, 11, 0, 0, TimeSpan.Zero),
            EndsAt: new DateTimeOffset(2026, 5, 4, 11, 45, 0, TimeSpan.Zero),
            LocalDate: new DateOnly(2026, 5, 4));

        var json = JsonSerializer.Serialize(response);

        foreach (var forbidden in ForbiddenSubstrings)
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheConfirmationStillCarriesEverythingACustomerNeeds()
    {
        // The other half, so the test above cannot be satisfied by returning nothing at all: a
        // reference to quote, who they are seeing, when, and which day the shop calls it.
        var properties = typeof(BookingConfirmedResponse).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains(nameof(BookingConfirmedResponse.BookingId), properties);
        Assert.Contains(nameof(BookingConfirmedResponse.WorkerId), properties);
        Assert.Contains(nameof(BookingConfirmedResponse.StartsAt), properties);
        Assert.Contains(nameof(BookingConfirmedResponse.EndsAt), properties);
        Assert.Contains(nameof(BookingConfirmedResponse.LocalDate), properties);
    }

    [Fact]
    public void TimestampsGoOutAsIso8601WithAnExplicitOffset()
    {
        // date-and-time.md rule: on the wire, an instant carries its offset. A customer in another
        // zone renders it locally; this product deliberately stores that zone nowhere (adr/0049).
        var response = new BookingConfirmedResponse(
            Guid.Empty, Guid.Empty,
            new DateTimeOffset(2026, 5, 4, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 4, 11, 45, 0, TimeSpan.Zero),
            new DateOnly(2026, 5, 4));

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("2026-05-04T11:00:00+00:00", json, StringComparison.Ordinal);
    }
}
