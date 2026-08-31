using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.AccessControl;

/// <summary>Precise about why, the same call `20-04`'s <c>BookingLifecycleErrors</c> and `20-06`'s
/// <c>ConfigurationErrors</c> already make: every caller here is an operator who has already passed a
/// permission check.</summary>
public static class AccessControlErrors
{
    public static Error Forbidden(Permission permission) => new(
        "access.forbidden",
        $"This operator does not hold '{permission.Value}' for this tenant.");

    public static Error OperatorNotFound(OperatorId operatorId) => new(
        "access.not_found", $"No operator {operatorId.Value} in this tenant.");

    public static Error RoleNotFound(RoleId roleId) => new(
        "access.not_found", $"No role {roleId.Value} in this tenant.");

    public static Error Invalid(string reason) => new("access.invalid", reason);

    /// <summary>`20-12`'s own account-owner invariant, surfaced from
    /// <see cref="AccountOwnerRoleException"/> as an ordinary rejection - the same translation
    /// <c>CreateCalendarHandler</c> makes for a domain constructor's <c>ArgumentException</c>, so a
    /// rule the aggregate enforces never reaches the API as an unhandled exception dressed as a
    /// 500.</summary>
    public static Error AccountOwnerRequiresContactAccess(string reason) =>
        new("access.account_owner_requires_contact_access", reason);
}
