namespace Ago.Calendar.Domain;

/// <summary>
/// A named <c>resource:action</c> permission (adr/0016's pattern), in this product's own,
/// independent vocabulary. adr/0027 is explicit that there is no shared <c>Permission</c> enum and no
/// shared <c>roles</c> table across the two products: AGO Chat's <c>conversation:*</c>/<c>site:*</c>
/// catalogue and this one never meet, and a permission granted in one console grants nothing in the
/// other. That is the stated cost of the decision, not an oversight to reconcile later.
///
/// <para>Only the permissions this product's own screens will actually check exist here.
/// <see cref="CalendarConfigure"/> is the exception worth naming: it has no caller until `20-06`
/// builds the configuration console, and it is declared now because the v1 seeded role below has to
/// say something about configuration one way or the other - leaving it out would have been a silent
/// decision rather than a stated one.</para>
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
