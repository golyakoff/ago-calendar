using System.Security.Cryptography;
using System.Text;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

/// <summary>
/// This product's own operator identity, never <c>Ago.Chat.Domain.OperatorId</c> (adr/0027). The two
/// name the same human being and never the same row; Keycloak's <c>sub</c> is the only thing they
/// share, resolved separately by each product.
///
/// <para><b>`22-05`/`adr/0093`: no longer a database-assigned primary key.</b> There is no local
/// `operators` table left to assign one from - this product holds no identity of its own any more.
/// It is instead a deterministic function of the Keycloak `sub` that names the same person on every
/// request (<c>OperatorIdentityClaimsTransformation</c>), computed with no lookup and no I/O: the
/// same subject always derives the same id, so nothing has to be stored to make "this id" mean "this
/// person" twice in a row. RFC 9562 s.5.5's name-based UUID version 5 (SHA-1 over a namespace and a
/// name) - the standard, reviewer-recognisable way to turn a name into a stable UUID, not a
/// hand-invented scheme. Hand-implemented rather than a BCL call: no .NET runtime as of this item
/// exposes a version-5 constructor (only <c>Guid.CreateVersion7</c> shipped, a time-ordered scheme
/// with no name-based mode). This is not <c>Guid.NewGuid()</c> in disguise (CLAUDE.md rule 2 bans
/// that in Domain for its non-determinism, not for touching <see cref="Guid"/> at all) - the same
/// input always produces the same output, with no hidden state and no I/O to make it
/// untestable.</para>
///
/// <para><b>Why keep the type at all, instead of threading a bare <see cref="string"/> subject through
/// every one of Application's permission-touching files.</b> Every call site downstream of
/// authentication only ever treats this value opaquely - passed to
/// <c>Application.Abstractions.IPermissionChecker</c>, logged, compared for equality - never displayed
/// as "the record with this id" or joined against anything else. A mechanical rename would touch
/// dozens of files to carry a <see cref="string"/> that means exactly what this <see cref="Guid"/>
/// already means, for no behavioural gain; keeping the type and changing only its provenance is the
/// smaller, equally honest change - the identical judgement `22-03`'s own remarks make for
/// <see cref="TenantId"/>: "a strongly-typed wrapper does not care where its value came from".</para>
///
/// <para><b>Local to this product.</b> The derived value has no meaning to AGO Chat or to any other
/// consumer of the same subject - it is this product's own opaque label for "whichever person this
/// `sub` names", nothing more.</para>
/// </summary>
public readonly record struct OperatorId(Guid Value) : IStronglyTypedId
{
    // A fixed, arbitrary namespace GUID (RFC 9562 s.6.5) - any fixed value works, as long as it never
    // changes: changing it would silently re-derive a different OperatorId for every existing subject.
    private static readonly Guid Namespace = new("2f5b6a3e-2f0a-4e9c-9a1d-2f0a4e9c9a1d");

    public static OperatorId FromExternalSubjectId(string externalSubjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubjectId);
        return new OperatorId(CreateNameBasedUuidVersion5(Namespace, Encoding.UTF8.GetBytes(externalSubjectId)));
    }

    /// <summary>
    /// RFC 9562 s.5.5, hand-implemented (this type's own remarks explain why): <c>SHA1(namespace ||
    /// name)</c>, the version/variant nibbles overwritten per the spec, name-based and deterministic.
    /// </summary>
    private static Guid CreateNameBasedUuidVersion5(Guid @namespace, byte[] name)
    {
        Span<byte> namespaceBytes = stackalloc byte[16];
        @namespace.TryWriteBytes(namespaceBytes);
        // .NET's in-memory Guid layout stores its first three fields little-endian; RFC 9562's own
        // byte representation of a UUID is big-endian throughout. Both conversions below undo exactly
        // that mismatch - once going in, once coming back out.
        SwapFieldByteOrder(namespaceBytes);

        var hashInput = new byte[namespaceBytes.Length + name.Length];
        namespaceBytes.CopyTo(hashInput);
        name.CopyTo(hashInput, namespaceBytes.Length);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(hashInput, hash);

        Span<byte> result = stackalloc byte[16];
        hash[..16].CopyTo(result);

        result[6] = (byte)((result[6] & 0x0F) | 0x50); // version 5
        result[8] = (byte)((result[8] & 0x3F) | 0x80); // RFC 9562 variant

        SwapFieldByteOrder(result);
        return new Guid(result);
    }

    private static void SwapFieldByteOrder(Span<byte> guidBytes)
    {
        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}
