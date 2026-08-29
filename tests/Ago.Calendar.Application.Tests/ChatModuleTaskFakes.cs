using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>Plain in-memory load/save, matching what the real adapter does - see
/// <see cref="IChatBookingTaskStore"/>'s own remarks for why there is no compare-and-set to fake
/// here, unlike <see cref="FakeBookingStore"/>.</summary>
internal sealed class FakeChatBookingTaskStore : IChatBookingTaskStore
{
    private readonly Dictionary<ChatBookingTaskId, ChatBookingTask> _tasks = [];

    public Task<ChatBookingTask?> GetByIdAsync(ChatBookingTaskId id, CancellationToken cancellationToken) =>
        Task.FromResult(_tasks.GetValueOrDefault(id));

    public Task AddAsync(ChatBookingTask task, CancellationToken cancellationToken)
    {
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }

    public Task SaveAsync(ChatBookingTask task, CancellationToken cancellationToken)
    {
        _tasks[task.Id] = task;
        return Task.CompletedTask;
    }
}
