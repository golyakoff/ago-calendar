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

    /// <summary>
    /// `22-14`/`adr/0100`: authenticated by Keycloak, and nothing more.
    ///
    /// <para>Every operator-facing route carries <see cref="OperatorPolicy"/> and must keep doing so.
    /// This one exists for the single route that has to be answerable <i>before</i> a tenant is
    /// resolved - <c>GET /api/v1/me/tenancies</c>, which tells the console which tenants there are to
    /// choose between. A person with calendar grants on two accounts fails
    /// <see cref="OperatorPolicy"/> by construction until they name one
    /// (<c>OperatorIdentityClaimsTransformation</c>), so gating that read behind it would be a
    /// chicken-and-egg refusal: you may not ask what you can act in until you can already act.</para>
    ///
    /// <para><b>Weaker is not unguarded.</b> The handler behind it never takes a tenant from the
    /// caller at all - it reads the validated token's own <c>sub</c> and answers only about that
    /// subject's own projection rows, so there is no tenant here for a caller to name wrongly.</para>
    /// </summary>
    public const string IdentityPolicy = "calendar-identity";
}
