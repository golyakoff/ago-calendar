namespace Ago.Calendar.Domain;

/// <summary>
/// An amount of money, stored as whole minor units (kopecks) rather than a <c>decimal</c> - the same
/// reasoning <see cref="Service"/>'s own <c>Duration</c> already applies to time: an integer is exact,
/// trivially comparable and orderable in SQL, and immune to the rounding a <c>decimal</c>/<c>double</c>
/// display can silently introduce on a value people expect to add up exactly. <see cref="CurrencyCode"/>
/// travels with every amount rather than being assumed from context, so a value is never interpreted by
/// a reader who does not also hold the currency it names (`23-35`).
///
/// <para><b>v1 accepts exactly one currency, <see cref="RubleCode"/>.</b> Nothing here makes a second
/// currency hard to add later - widening <see cref="Create"/>'s accepted list is a one-line change,
/// and the shape (amount + code, stored together) is already the shape a real multi-currency tenant
/// would need. But nothing before this type needed a second currency: this product's every tenant so
/// far is a Russian small business, and building acceptance for a currency nobody has asked for is the
/// premature generalisation <c>clean-architecture.md</c> warns a platform-shaped type against - the
/// difference here is this is a product-shaped type in <c>Ago.Calendar.Domain</c>, but the same
/// discipline applies: a "which currency does this tenant bill in" setting is a real feature with its
/// own UI and its own decision about whether a tenant may ever hold two, not a byproduct of two columns
/// on <c>services</c>.</para>
///
/// <para><b>Zero is a legitimate amount</b> - a free consultation is a real price, not the absence of
/// one; <see cref="Service.Price"/> being <see langword="null"/> is what "no price" means, so
/// <see cref="Create"/> only rejects negative amounts.</para>
/// </summary>
public readonly record struct Money
{
    public const string RubleCode = "RUB";

    /// <summary>100,000.00 in <see cref="RubleCode"/>'s own minor unit - far above any real service
    /// price this product's tenants charge, and a ceiling for the same reason
    /// <see cref="Service"/>'s own <c>MaxDurationMinutes</c> has one: a typo four digits too long
    /// should fail at entry, not display as a price nobody meant to type.</para>
    /// </summary>
    public const int MaxMinorUnits = 100_000_00;

    public int MinorUnits { get; }

    public string CurrencyCode { get; }

    private Money(int minorUnits, string currencyCode)
    {
        MinorUnits = minorUnits;
        CurrencyCode = currencyCode;
    }

    public static Money Create(int minorUnits, string currencyCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currencyCode);

        if (minorUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minorUnits), minorUnits, "A price cannot be negative.");
        }

        if (minorUnits > MaxMinorUnits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minorUnits), minorUnits, $"A price is capped at {MaxMinorUnits} minor units.");
        }

        if (!string.Equals(currencyCode, RubleCode, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currencyCode), currencyCode, $"Only '{RubleCode}' is accepted in v1.");
        }

        return new Money(minorUnits, currencyCode);
    }

    /// <summary>The only factory a caller inside this product needs today - see the type's own remarks
    /// on why <see cref="RubleCode"/> is not yet a caller-supplied choice.</summary>
    public static Money Rubles(int minorUnits) => Create(minorUnits, RubleCode);
}
