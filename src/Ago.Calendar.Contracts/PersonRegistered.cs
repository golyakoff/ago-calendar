namespace Ago.Calendar.Contracts;

/// <summary>
/// `adr/0184` decision 2: a booking with <b>no</b> chat origin (the public widget, an operator-entered
/// booking) minted a person id locally, and this is how the account's person registry - AGO Chat -
/// learns that the person exists. Published through the outbox in the same transaction as the claim
/// that minted the id (CLAUDE.md rule 4); never a synchronous call to chat on the write path.
///
/// <para><b>Wire shape, shared by copy.</b> <c>ago-chat</c> declares its own
/// <c>PersonRegisteredWireContract</c> with these exact property names - PascalCase, because this
/// record is serialised with <see cref="System.Text.Json.JsonSerializer"/>'s default options - the
/// identical arms-length treatment every other cross-product event in this codebase gets. Changing a
/// field here changes it there too, deliberately by hand.</para>
///
/// <para><b>What it carries and what it does not.</b> The person id (the durable truth), the account,
/// the phone the person booked with and the name they typed, if any - the minimum chat needs to
/// create a Person a console can later show. No booking details: a person is not a booking, and the
/// registry has no reason to know what was booked. A redelivery is harmless - the consumer is
/// idempotent on <see cref="PersonId"/> (CLAUDE.md rule 5).</para>
/// </summary>
/// <param name="PersonId">The id this product minted and stamped onto every claimed row's own
/// <c>person_id</c>. Chat creates its Person under exactly this id, so the calendar's reference and
/// chat's registry agree by construction.</param>
/// <param name="AccountId">The account (chat's <c>site_id</c>, this product's <c>tenant_id</c> -
/// `adr/0093` made them the same value) the person belongs to.</param>
/// <param name="Phone">E.164, the same canonical form <c>PhoneNumber.Value</c> holds.</param>
/// <param name="Name">What the person typed as their name, trimmed, or <see langword="null"/> when
/// they typed nothing.</param>
public sealed record PersonRegistered(
    Guid PersonId,
    Guid AccountId,
    string Phone,
    string? Name,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);
