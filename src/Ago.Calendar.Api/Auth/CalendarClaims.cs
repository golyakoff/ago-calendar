namespace Ago.Calendar.Api.Auth;

/// <summary>
/// The two claims this product adds to a validated Keycloak principal, and the policy that requires
/// them.
///
/// <para><b>These are AGO Calendar's own claim names on AGO Calendar's own principal.</b> They look
/// like AGO Chat's (<c>operator_id</c>, and a tenant claim beside it) because both products copied
/// adr/0022's shape - which adr/0027 says is the cost it knowingly accepted. They are not the same
/// claims: the id inside is a row in <i>this</i> product's <c>operators</c> table, in a database AGO
/// Chat cannot reach, and a token that carries AGO Chat's <c>operator_id</c> means nothing here
/// because this transformation never reads an incoming claim of that name - it writes one.</para>
/// </summary>
public static class CalendarClaims
{
    public const string OperatorId = "operator_id";

    public const string TenantId = "tenant_id";

    /// <summary>
    /// The authorization policy every operator-facing endpoint carries.
    ///
    /// <para><b>Requiring the claim, not merely authentication, is the point</b> (adr/0022's own
    /// wording): a validated Keycloak token whose <c>sub</c> matches no row here is a real person who
    /// is simply not an operator of this product, and they must be refused at the door. Without this,
    /// the refusal would happen further in, when something tried to read a claim that was never
    /// added - a <c>NullReferenceException</c> dressed as a 500 instead of a 403.</para>
    /// </summary>
    public const string OperatorPolicy = "calendar-operator";
}
