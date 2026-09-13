using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.TenantExport;

/// <summary>
/// `22-31`: a thin pass-through to <see cref="ITenantDataExporter"/>, the identical shape
/// <see cref="TenantErasure.EraseTenantDataHandler"/>'s own remarks give for its sibling - so
/// <c>Ago.Calendar.Api</c> depends on <c>Ago.Calendar.Application</c>, never on
/// <c>Ago.Calendar.Infrastructure.Postgres</c> directly, and so a future business rule about this call
/// (a rate limit, an audit log entry) has somewhere to go that is not the endpoint itself.
///
/// <para><b>Takes a <see cref="Stream"/> alongside the command, unlike every other handler in this
/// codebase.</b> Every sibling handler computes a value and returns it; this one's whole job is to
/// stream bytes into a caller-supplied sink (the endpoint's own <c>HttpResponse.Body</c>) without ever
/// holding the tenant's full export in memory to hand back as a return value -
/// <see cref="ITenantDataExporter"/>'s own remarks explain why that streaming property is the point.
/// </para>
/// </summary>
public sealed class ExportTenantDataHandler(ITenantDataExporter exporter)
{
    public int FormatVersion => exporter.FormatVersion;

    public string ContentType => exporter.ContentType;

    public Task HandleAsync(ExportTenantData command, Stream destination, CancellationToken cancellationToken) =>
        exporter.WriteAsync(command.TenantId, destination, cancellationToken);
}

public sealed record ExportTenantData(TenantId TenantId);
