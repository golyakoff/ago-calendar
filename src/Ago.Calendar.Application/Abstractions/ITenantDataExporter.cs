using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-31`: this product's own half of a tenant's export - CLAUDE.md rule 2's "every external resource
/// sits behind a port" applied to the read side the same way <see cref="ITenantErasureRepository"/>
/// already applies it to the destructive one. Deliberately narrow, for the identical reason that port
/// is narrow rather than a third method on <see cref="ITenantRepository"/>: a whole-tenant read across
/// every table this product holds is not shaped by any single-aggregate use case, so it earns its own
/// port rather than stretching an existing one.
///
/// <para><b>What crosses to AGO Chat is this product's own choice, opaque on the other side.</b>
/// `adr/0149` rule 3 - chat never parses a module's data - is what frees this port to pick whatever
/// on-disk shape suits this product; see <see cref="Infrastructure.Postgres.TenantDataExportWriter"/>'s
/// own remarks for the concrete format chosen and why. AGO Chat's own
/// `Ago.Chat.Application.Abstractions.IModuleRegistrationGateway.ExportTenantDataAsync` receives exactly
/// the bytes <see cref="WriteAsync"/> writes, plus <see cref="FormatVersion"/> carried alongside them -
/// never parsed, only stored and counted.</para>
///
/// <para><b>Streams onto the caller's own destination, never returns a buffered array.</b> The backlog
/// item's own Answered section accepts "a whole tenant's calendar history in one call" as this design's
/// operational cost, but that is a statement about the wire being one request/response, not license to
/// hold the whole payload in this process's memory at once - see
/// <see cref="Infrastructure.Postgres.TenantDataExportWriter"/>'s own remarks for how this method keeps
/// the two apart.</para>
/// </summary>
public interface ITenantDataExporter
{
    /// <summary>This product's own format version for the bytes <see cref="WriteAsync"/> writes - a
    /// constant today, exposed as a property (not a literal at every call site) so a future format
    /// change has exactly one place that decides the number, matching
    /// <c>Ago.Chat.Worker.SiteExportArchiveWriter.FormatVersion</c>'s own "the writer states its own
    /// version" shape for the sibling product's identical decision.</summary>
    int FormatVersion { get; }

    /// <summary>The wire-visible content type of the bytes <see cref="WriteAsync"/> writes -
    /// <c>"application/zip"</c> today - exposed here rather than hardcoded at the one call site that
    /// needs it (<c>ModuleRegistrationEndpoints</c>) so the writer and the endpoint can never
    /// disagree.</summary>
    string ContentType { get; }

    /// <summary>
    /// Writes this tenant's whole exportable history to <paramref name="destination"/>, streaming as it
    /// goes rather than buffering the result first.
    ///
    /// <para><b>A tenant with no row at all is not an error.</b> Already erased (`22-30`), or never
    /// provisioned in the first place - both converge here exactly the way
    /// <see cref="ITenantErasureRepository.EraseAsync"/>'s own idempotent "not found is not a failure"
    /// answer already treats the identical two cases on the destructive side. This method still writes a
    /// complete, valid archive; every table's own member is simply empty. Distinguishing "this tenant
    /// never had a calendar" from "the calendar could not answer" is AGO Chat's own job, decided by
    /// whether it ever asked this endpoint at all (<c>Ago.Chat.Domain.EnabledModule</c>'s own row) - not
    /// a distinction this port makes or needs to.</para>
    /// </summary>
    Task WriteAsync(TenantId tenantId, Stream destination, CancellationToken cancellationToken);
}
