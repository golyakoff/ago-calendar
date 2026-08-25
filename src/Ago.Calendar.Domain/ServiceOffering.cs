namespace Ago.Calendar.Domain;

/// <summary>One row of <c>worker_services</c> - which worker performs which service. Owned by
/// <see cref="Worker"/>, created only by <see cref="Worker.Offer"/>.</summary>
public sealed class ServiceOffering
{
    public WorkerId WorkerId { get; private set; }

    public ServiceId ServiceId { get; private set; }

    internal ServiceOffering(WorkerId workerId, ServiceId serviceId)
    {
        WorkerId = workerId;
        ServiceId = serviceId;
    }

    // EF Core materialization only - never called by domain code.
    private ServiceOffering()
    {
    }
}
