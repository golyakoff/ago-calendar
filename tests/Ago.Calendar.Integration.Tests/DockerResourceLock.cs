namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// A machine-wide lock so that this repository's Testcontainers fixtures never have their containers
/// alive at the same time as another repository's - several background workers, each in their own
/// git worktree, running <c>dotnet test</c> concurrently is the normal state of this project
/// (testing.md).
///
/// <para>Deliberately the *same lock file name* <c>ago-chat</c> uses, not a per-repository one. The
/// resource being protected is one Docker daemon's CPU and memory, and it does not care which
/// product asked; two repositories each holding "their own" lock would leave the contention exactly
/// as it was. This is the one place where copying a file across repositories would have been the
/// wrong instinct - the copy has to share the other one's key to work at all.</para>
///
/// <para>Implemented as an exclusive-open lock file rather than a named <see cref="Semaphore"/>:
/// named kernel objects are Windows-only in .NET and throw
/// <see cref="PlatformNotSupportedException"/> on the Linux runner CI actually uses. A file opened
/// with <see cref="FileShare.None"/> gives the same mutual exclusion everywhere and releases itself
/// if the holding process dies.</para>
/// </summary>
public static class DockerResourceLock
{
    private static readonly string LockFilePath = Path.Combine(Path.GetTempPath(), "ago-chat-testcontainers.lock");

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var stream = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new Releaser(stream);
            }
            catch (IOException)
            {
                // Somebody else holds it - ordinary contention, not an error. Poll rather than block
                // a thread; this runs inside an async fixture lifecycle.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private sealed class Releaser(FileStream stream) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            stream.Dispose();
        }
    }
}
