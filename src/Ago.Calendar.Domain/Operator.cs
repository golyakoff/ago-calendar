namespace Ago.Calendar.Domain;

/// <summary>
/// The day-to-day actor: the person who works the pending-booking queue, confirms or vetoes a
/// claim, cancels a visit and keeps a customer's lead card. Created by a <see cref="Tenant"/>.
///
/// <para><b>Genuinely a new entity, not a reference to AGO Chat's operator</b> (adr/0027). The
/// visible proof is what is missing: there is no <c>Capacity</c> and no <c>ActiveChats</c> here.
/// AGO Chat's operator has a concurrent-conversation limit because a human can hold only so many
/// live conversations at once; a booking queue is a work list, not a set of simultaneous
/// attachments, so the concept does not transfer. That absence is the concrete reason a shared
/// <c>Operator</c> aggregate was rejected - it would have needed a per-product escape hatch on its
/// first day.</para>
///
/// <para><b>Presence is absent too, and for a different reason.</b> AGO Chat's operator carries an
/// online/away/offline status because routing a live conversation depends on it. Nothing in this
/// product routes to a *particular* operator: the spec's v1 queue is shared across all of a
/// tenant's operators, exactly so that whoever is around handles whatever arrived. A status column
/// with no reader would be a guess about `20-04`, so it is not here.</para>
/// </summary>
public sealed class Operator
{
    private readonly List<RoleAssignment> _roles = [];

    public OperatorId Id { get; }

    public TenantId TenantId { get; }

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// The Keycloak-issued <c>sub</c> claim identifying this operator to the IdP - what this
    /// product's own <c>OperatorIdentityClaimsTransformation</c> (`20-06`, adr/0022's shape copied
    /// per adr/0027) resolves a validated OIDC token against. Nullable and unique-when-present: the
    /// same person may hold an <c>Ago.Chat</c> operator row with the same <c>sub</c>, and the two
    /// rows never learn about each other.
    /// </summary>
    public string? ExternalSubjectId { get; private set; }

    /// <summary>
    /// `adr/0088`: the address a tenant typed on the Access screen's "invite a colleague" form, set
    /// only for an operator provisioned that way - <see cref="ExternalSubjectId"/> is null at the same
    /// moment, and the console's own Invited/Active status column is exactly that pairing read back.
    /// Never cleared by <see cref="LinkExternalIdentity"/>: once the person signs in the field becomes
    /// a historical "who was this invite for" rather than a live lookup key, and there is no reason to
    /// destroy that record the moment it stops being needed for matching.
    /// </summary>
    public InvitedEmail? InvitedEmail { get; private set; }

    public IReadOnlyList<RoleAssignment> Roles => _roles;

    /// <summary>
    /// `20-12`: the tenant's own account owner - operationally, the first operator ever created for a
    /// tenant (`RegisterTenantHandler`'s own provisioning transaction is the only caller that passes
    /// <see langword="true"/> today). Settable only here, at construction, and never again - there is
    /// no <c>Rename</c>-shaped mutator for it, on purpose, the same way <c>Id</c> and <c>TenantId</c>
    /// carry no setter: "who provisioned this tenant" is a fact about how the row came to exist, not a
    /// state a later request should be able to flip.
    ///
    /// <para>Deliberately not AGO's cross-tenant platform-owner concept (adr/0032) and not anything
    /// `13-07`'s flat, tenant-local role model already names - see `20-12`'s own item file for why
    /// neither applies here. This is a narrower, Calendar-only idea: one operator per tenant who is
    /// guaranteed, by <see cref="Grant"/>/<see cref="Revoke"/>'s own invariant below, to always hold a
    /// role granting <see cref="Permission.CustomerRead"/>.</para>
    /// </summary>
    public bool IsAccountOwner { get; }

    private Operator(
        OperatorId id, TenantId tenantId, string displayName, string? externalSubjectId, bool isAccountOwner,
        InvitedEmail? invitedEmail)
    {
        Id = id;
        TenantId = tenantId;
        DisplayName = displayName;
        ExternalSubjectId = externalSubjectId;
        IsAccountOwner = isAccountOwner;
        InvitedEmail = invitedEmail;
    }

    // EF Core materialization only - never called by domain code.
    private Operator()
    {
    }

    /// <param name="isAccountOwner">See <see cref="IsAccountOwner"/>. Defaults to
    /// <see langword="false"/> because every path that creates an operator is "some other way" except
    /// one - <c>RegisterTenantHandler</c>'s own provisioning transaction is the only caller expected to
    /// pass <see langword="true"/>.</param>
    /// <param name="invitedEmail">See <see cref="InvitedEmail"/>. Null for every caller except
    /// <c>InviteOperatorHandler</c> (`adr/0088`) - the account owner is created with its subject already
    /// known, never through an invite, so the two fields are never both set by any caller in this
    /// codebase today. Nothing here forbids it, on purpose: the invariant that actually matters is
    /// "invited XOR resolvable", which <see cref="ExternalSubjectId"/> alone already expresses, and
    /// adding a second rule that says the same thing about this field would be redundant rather than
    /// protective.</param>
    public static Operator Create(
        OperatorId id,
        TenantId tenantId,
        string displayName,
        string? externalSubjectId = null,
        bool isAccountOwner = false,
        InvitedEmail? invitedEmail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (externalSubjectId is not null && string.IsNullOrWhiteSpace(externalSubjectId))
        {
            throw new ArgumentException(
                "An external subject id is either absent or a real value, never blank.", nameof(externalSubjectId));
        }

        return new Operator(id, tenantId, displayName.Trim(), externalSubjectId, isAccountOwner, invitedEmail);
    }

    /// <summary>
    /// Takes the whole <see cref="Role"/> rather than a <see cref="RoleId"/>, and that is the point:
    /// an id alone cannot answer "does this role belong to my tenant", so the cross-tenant check
    /// would have to move to the caller - at which point it stops being an invariant and becomes a
    /// convention every future call site must remember.
    /// </summary>
    public void Grant(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (role.TenantId != TenantId)
        {
            throw new TenantMismatchException(
                $"Role {role.Id.Value} belongs to tenant {role.TenantId.Value}, operator {Id.Value} to {TenantId.Value}.");
        }

        // Granting twice is a no-op, not an error - the same "the caller already got what they asked
        // for" shape Ago.Chat.Domain.Conversation.AssignTo established for a re-join after a
        // reconnect. Provisioning is exactly the kind of step that gets retried.
        if (_roles.Exists(assignment => assignment.RoleId == role.Id))
        {
            return;
        }

        var grantsCustomerRead = role.Grants(Permission.CustomerRead);

        // `20-12`'s account-owner invariant. Grant only ever adds capability, so this can only fire in
        // one real sequence: the account owner currently holds no role granting CustomerRead at all
        // (nothing has been granted yet, or every prior grant also lacked it) and the role about to be
        // granted does not carry it either - meaning this grant would still leave them without one.
        // Checked before the list is mutated, so a refusal here changes nothing about the operator's
        // state.
        if (IsAccountOwner && !grantsCustomerRead && !_roles.Exists(assignment => assignment.GrantsCustomerRead))
        {
            throw new AccountOwnerRoleException(
                $"Operator {Id.Value} is tenant {TenantId.Value}'s account owner and must always hold a " +
                $"role granting '{Permission.CustomerRead.Value}'; granting '{role.Name}' alone would not " +
                "give them one.");
        }

        _roles.Add(new RoleAssignment(Id, role.Id, grantsCustomerRead));
    }

    /// <summary>
    /// The counterpart <see cref="Grant"/> never had. Takes the whole <see cref="Role"/> for the same
    /// reason <see cref="Grant"/> does - a bare <see cref="RoleId"/> cannot answer "does this role
    /// belong to my tenant".
    /// </summary>
    public void Revoke(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);

        if (role.TenantId != TenantId)
        {
            throw new TenantMismatchException(
                $"Role {role.Id.Value} belongs to tenant {role.TenantId.Value}, operator {Id.Value} to {TenantId.Value}.");
        }

        var assignment = _roles.Find(existing => existing.RoleId == role.Id);
        if (assignment is null)
        {
            // Revoking a role never held is a no-op, mirroring Grant's own idempotency - "the caller
            // already has what they asked for" holds in both directions.
            return;
        }

        // `20-12`'s account-owner invariant, the direction Revoke actually exists to guard: refuse to
        // remove the one remaining role that grants CustomerRead from the tenant's own account owner.
        // Any other revoke - a role that never carried CustomerRead, or one that did while another
        // held role still does - is unaffected.
        if (IsAccountOwner
            && assignment.GrantsCustomerRead
            && !_roles.Exists(existing => existing.RoleId != role.Id && existing.GrantsCustomerRead))
        {
            throw new AccountOwnerRoleException(
                $"Operator {Id.Value} is tenant {TenantId.Value}'s account owner and must always hold a " +
                $"role granting '{Permission.CustomerRead.Value}'; revoking '{role.Name}' would leave them " +
                "without one.");
        }

        _roles.Remove(assignment);
    }

    /// <summary>Links this operator to a Keycloak subject the first time they sign in. Re-linking to
    /// a *different* subject is rejected: two identities resolving to one operator row is the exact
    /// ambiguity <c>external_subject_id</c>'s unique index exists to prevent, and an aggregate that
    /// allows it locally only defers the failure to the database.</summary>
    public void LinkExternalIdentity(string externalSubjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubjectId);

        if (ExternalSubjectId is not null && ExternalSubjectId != externalSubjectId)
        {
            throw new InvalidOperationException(
                $"Operator {Id.Value} is already linked to a different external identity.");
        }

        ExternalSubjectId = externalSubjectId;
    }

    public void Rename(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
    }
}
