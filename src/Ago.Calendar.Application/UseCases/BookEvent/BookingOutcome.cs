using Ago.Calendar.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>
/// What <see cref="BookEventHandler"/> returns.
///
/// <para><b>Why not <see cref="Result{T}"/>, which every other handler in this product uses.</b>
/// <see cref="Ago.Platform.Kernel.Error"/> is <c>(Code, Message)</c> and has nowhere to carry
/// structured failure metadata. This endpoint owes a rate-limited caller a real
/// <c>Retry-After</c> header - api-design.md requires it in as many words ("returns <c>429</c> with
/// <c>Retry-After</c>, which the widget must honour with jittered backoff") - and the only place the
/// retry-after exists is the <c>RateLimitDecision</c> the handler received. Squeezing it into the
/// message text is what AGO Chat did, and <c>Ago.Chat.Api.Http.ErrorExtensions</c> carries a comment
/// apologising for the resulting missing header. Rather than repeat that, or widen
/// <see cref="Ago.Platform.Kernel.Error"/> (a platform change this item deliberately does not make),
/// this one handler returns a type with room for the field. The trade is one non-standard return
/// type against a header the protocol actually promises.</para>
/// </summary>
public readonly record struct BookingOutcome
{
    private BookingOutcome(BookingConfirmation? booking, Error? error, TimeSpan? retryAfter)
    {
        Booking = booking;
        Error = error;
        RetryAfter = retryAfter;
    }

    /// <summary>Set exactly when the claim succeeded.</summary>
    public BookingConfirmation? Booking { get; }

    /// <summary>Set exactly when it did not. A lost race is one of these - an ordinary, expected
    /// value, never an exception and never a 500.</summary>
    public Error? Error { get; }

    /// <summary>Meaningful only for <see cref="BookingErrors.RateLimited"/>; the endpoint turns it
    /// into the <c>Retry-After</c> header.</summary>
    public TimeSpan? RetryAfter { get; }

    public bool IsSuccess => Booking is not null;

    public static BookingOutcome Confirmed(BookingConfirmation booking) => new(booking, null, null);

    public static BookingOutcome Rejected(Error error) => new(null, error, null);

    public static BookingOutcome RateLimited(TimeSpan retryAfter) =>
        new(null, BookingErrors.RateLimited(retryAfter), retryAfter);
}
