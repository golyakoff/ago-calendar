namespace Ago.Calendar.Domain;

/// <summary>
/// A named <c>resource:action</c> permission (adr/0016's pattern).
///
/// <para><b>`22-05`/`adr/0093` superseded the "independent vocabulary" half of adr/0027.</b> These
/// seven strings are, byte for byte, the same seven strings <c>Ago.Chat.Domain.Permission</c> also
/// declares - the account side's own role catalogue absorbed this product's vocabulary unchanged,
/// because the two prefix sets (<c>booking:*</c>/<c>calendar:*</c>/<c>customer:*</c> against
/// <c>conversation:*</c>/<c>site:*</c>) never collided. A grant now happens on the account side and
/// is read here through the projection <c>RoleAssignmentsChanged</c> replicates
/// (<c>Application.Abstractions.IRoleAssignmentProjectionStore</c>) - "a permission granted in one
/// console grants nothing in the other" is exactly the sentence this item made false.</para>
///
/// <para><b>This type still exists locally, and that is deliberate, not leftover.</b> This product
/// ships as its own repository with its own database (adr/0027's surviving half); it has no
/// <c>ProjectReference</c> to <c>Ago.Chat.Domain</c> and must not gain one (adr/0012: the platform
/// ships as packages, and neither product's <c>Domain</c> ships as one at all). Keeping a local,
/// independently-declared <c>Permission</c> is what lets every handler in this product keep a typed
/// check with no cross-repository coupling - the wire-level agreement is that the strings match, not
/// that the types do.</para>
///
/// <para>Only the permissions this product's own screens actually check exist here.
/// <see cref="CalendarConfigure"/> is the exception worth naming: it gates
/// <c>ConsoleEndpoints.HandleSetAllowedOriginsAsync</c> and friends, and is declared here because a v1
/// role has to say something about configuration one way or the other - leaving it out would have
/// been a silent decision rather than a stated one.</para>
/// </summary>
public readonly record struct Permission(string Value)
{
    /// <summary>Approve a <see cref="EventStatus.PendingConfirmation"/> booking before its deadline
    /// (`20-04`).</summary>
    public static readonly Permission BookingConfirm = new("booking:confirm");

    /// <summary>Veto a pending booking inside the confirmation window (`20-04`). Separate from
    /// <see cref="BookingCancel"/> on purpose: rejecting inside the window and cancelling a
    /// confirmed visit are different acts with different consequences for the customer, and
    /// adr/0016's granularity argument is precisely that a future role may want one without the
    /// other.</summary>
    public static readonly Permission BookingReject = new("booking:reject");

    /// <summary>Cancel a booking that is already <see cref="EventStatus.Booked"/>.</summary>
    public static readonly Permission BookingCancel = new("booking:cancel");

    /// <summary>Mark a past visit as a no-show (`20-04`).</summary>
    public static readonly Permission BookingMarkNoShow = new("booking:mark_no_show");

    /// <summary>Read a customer's lead card - name, notes, booking history.</summary>
    public static readonly Permission CustomerRead = new("customer:read");

    /// <summary>Edit a lead card's name and notes.</summary>
    public static readonly Permission CustomerEdit = new("customer:edit");

    /// <summary>Configure calendars, workers, services and working hours. No caller until
    /// `20-06`.</summary>
    public static readonly Permission CalendarConfigure = new("calendar:configure");

    public override string ToString() => Value;
}
