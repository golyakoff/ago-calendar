namespace Ago.Calendar.Domain;

/// <summary>
/// `23-12`/`decisions.md` §5, `adr/0123`: this product's own copy of the account's contact-visibility
/// rung - independently declared, never shared, for the identical reason
/// <c>Ago.Calendar.Domain.Permission</c> restates for itself: this project has no
/// <c>ProjectReference</c> to <c>Ago.Chat.Domain</c> and must not gain one (adr/0012, adr/0027). The
/// account side's own <c>Ago.Chat.Domain.ContactVisibility</c> is the fact; this is this product's
/// own reading of it, replicated by <see cref="Application.Abstractions.IContactVisibilityProjectionStore"/>
/// from `ago-chat`'s <c>ContactVisibilityChanged</c> (`adr/0123`'s own minimal, cross-boundary
/// contract).
///
/// <para><b>Only two members, byte for byte the account side's own set. Rung three ("Never") is
/// absent, not merely unused.</b> `decisions.md` §5 is explicit that the third rung must not be sold
/// until the system can place the call itself (click-to-call, system-sent messages) - `adr/0123`
/// took that literally on the account side by never adding a member for it, and this type is this
/// product's own reading of the identical fact, so it carries the identical restriction: adding a
/// value here that the account side can never actually send would let this product's own code imply
/// a guarantee `ago-chat` has deliberately not built.</para>
/// </summary>
public enum ContactVisibility
{
    /// <summary>Everything visible - today's behaviour, and the value a tenant with no projected rung
    /// reads as (`23-11`/`23-12`'s own Scope: a tenant older than the event, or one the projection has
    /// not yet caught up to, must see exactly what it always has, not a stricter default earned by
    /// nothing more than a message not having arrived yet).</summary>
    Visible,

    /// <summary>Masked in every list read this product serves; a deliberate reveal returns the number
    /// and leaves a record naming the operator. `decisions.md` §5's own framing: attribution, not
    /// prevention.</summary>
    MaskedWithReveal,
}
