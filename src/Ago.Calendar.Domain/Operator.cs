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

    public IReadOnlyList<RoleAssignment> Roles => _roles;

    private Operator(OperatorId id, TenantId tenantId, string displayName, string? externalSubjectId)
    {
        Id = id;
        TenantId = tenantId;
        DisplayName = displayName;
        ExternalSubjectId = externalSubjectId;
    }

    // EF Core materialization only - never called by domain code.
    private Operator()
    {
    }

    public static Operator Create(
        OperatorId id, TenantId tenantId, string displayName, string? externalSubjectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (externalSubjectId is not null && string.IsNullOrWhiteSpace(externalSubjectId))
        {
            throw new ArgumentException(
                "An external subject id is either absent or a real value, never blank.", nameof(externalSubjectId));
        }

        return new Operator(id, tenantId, displayName.Trim(), externalSubjectId);
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

        _roles.Add(new RoleAssignment(Id, role.Id));
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
