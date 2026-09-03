using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-21` (`ago-root#339`): the reason the item exists, demonstrated rather than asserted - a host
/// started against a deliberately out-of-date database must refuse rather than serve 200s. Ported
/// unchanged in shape from <c>Ago.Chat.Integration.Tests.SchemaGuardRefusalTests</c> (`8-08`).
///
/// <para><b>A real process, not a <c>WebApplicationFactory</c>.</b> An in-process host built by a test
/// would prove the guard works in a host this test assembled - not that the host that actually ships
/// runs it. So this launches the published <c>Ago.Calendar.Api.dll</c> the same way
/// <c>SchemaGuardRefusalTests</c> launches <c>Ago.Chat.Api.dll</c>, and asserts on the process's exit
/// code and on whether anything ever answered on its port.</para>
///
/// <para><b>What this deliberately does not do</b> is bring up Redis or Keycloak so the host can reach
/// a fully-started state. It does not need to: the guard runs between <c>builder.Build()</c> and
/// <c>app.Run()</c>, so on the refusal path the process exits before Kestrel starts listening and
/// before any <c>ValidateOnStart</c> options check or hosted service runs - which is exactly the
/// ordering claim being tested. The positive control below asserts the opposite half in the same way,
/// by reading the log line the guard emits on success, and then stops caring what the host does
/// next.</para>
/// </summary>
[Collection(SchemaCollection.Name)]
public class SchemaGuardRefusalTests(SchemaFixture fixture)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The failure `20-21` closes, reproduced: every page would return 200 while every query touching
    /// a column the pending migration was supposed to add fails. Now nothing returns anything, because
    /// nothing starts.
    /// </summary>
    [Fact]
    public async Task AHostStartedAgainstAnOutOfDateSchema_RefusesToStart()
    {
        await fixture.ResetToOneMigrationBehindAsync();
        var port = GetFreeTcpPort();

        var run = await RunApiAsync(port, waitTimeout: "00:00:00");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(nameof(Ago.Calendar.Infrastructure.Postgres.Schema.SchemaOutOfDateException),
            run.Output, StringComparison.Ordinal);
        // Names the migration it is missing - the half that turns "some queries fail" into a cause.
        Assert.Contains(fixture.AllMigrations[^1], run.Output, StringComparison.Ordinal);
        // And, the actual claim: nothing ever answered on the port. A refusal that still served would
        // be the incident with extra logging.
        Assert.False(await AnythingListeningAsync(port));
    }

    /// <summary>
    /// The positive control, without which the test above would pass just as well against a host that
    /// refuses to start for any reason at all. Same binary, same environment, same port - only the
    /// schema differs - and this time the guard reports the schema current and the process does not
    /// exit on its account.
    /// </summary>
    [Fact]
    public async Task AHostStartedAgainstACurrentSchema_PassesTheGuard()
    {
        await fixture.ResetToCurrentAsync();
        var port = GetFreeTcpPort();

        var run = await RunApiAsync(port, waitTimeout: "00:00:00", killAfter: TimeSpan.FromSeconds(20));

        Assert.Contains("Schema is current", run.Output, StringComparison.Ordinal);
        Assert.Contains(fixture.AllMigrations[^1], run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(Ago.Calendar.Infrastructure.Postgres.Schema.SchemaOutOfDateException),
            run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wait, end to end and in a real process: the host starts while the schema is still behind,
    /// something applies it a moment later, and the host proceeds instead of dying. This is the
    /// behaviour that makes "the migrator Job and the Deployments are applied together" a supported
    /// deploy rather than a race, once `#312` actually runs one - and it is the reason `20-21` needs no
    /// init container to be correct, only to be belt-and-braces.
    /// </summary>
    [Fact]
    public async Task AHostThatStartsBeforeTheMigrator_WaitsForItRatherThanFailing()
    {
        await fixture.ResetToOneMigrationBehindAsync();
        var port = GetFreeTcpPort();

        var hostTask = RunApiAsync(port, waitTimeout: "00:00:45", killAfter: TimeSpan.FromSeconds(40));

        // Let the host reach its first check and start waiting, then be the migrator arriving late.
        await Task.Delay(TimeSpan.FromSeconds(5));
        await using (var writer = new StringWriter())
        {
            var exitCode = await Migrator.MigratorRunner.RunAsync(
                fixture.ConnectionString, Migrator.MigratorMode.Apply, writer, CancellationToken.None);
            Assert.Equal(Migrator.MigratorRunner.Success, exitCode);
        }

        var run = await hostTask;

        Assert.Contains("Waiting up to", run.Output, StringComparison.Ordinal);
        Assert.Contains("Schema reached the expected version", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(Ago.Calendar.Infrastructure.Postgres.Schema.SchemaOutOfDateException),
            run.Output, StringComparison.Ordinal);
    }

    private sealed record ProcessRun(int ExitCode, string Output);

    /// <summary>
    /// Launches the published <c>Ago.Calendar.Api.dll</c>. Every environment variable below exists
    /// because a piece of this host's own startup reads it eagerly, before <c>builder.Build()</c> -
    /// none of the targets is reachable, and none needs to be: on the refusal path the process never
    /// gets far enough to dial anything, and on the success path this stops reading once the guard has
    /// spoken.
    /// </summary>
    private async Task<ProcessRun> RunApiAsync(int port, string waitTimeout, TimeSpan? killAfter = null)
    {
        var apiDll = Path.Combine(AppContext.BaseDirectory, "Ago.Calendar.Api.dll");
        Assert.True(File.Exists(apiDll), $"Expected the Api host next to this test assembly at {apiDll}.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{apiDll}\" --urls http://127.0.0.1:{port}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["AGO_CALENDAR_CONNECTION_STRING"] = fixture.ConnectionString,
                    ["SchemaGuard__WaitTimeout"] = waitTimeout,
                    ["SchemaGuard__PollInterval"] = "00:00:01",
                    // Production, not Development: Development turns on ValidateOnBuild, which walks
                    // every registered service's constructor graph before a single line of this host's
                    // own startup runs - and would therefore fail for reasons that have nothing to do
                    // with the schema. Production is also what a deployed pod runs.
                    ["DOTNET_ENVIRONMENT"] = "Production",
                    ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    // Read eagerly by AddCalendarOperatorAuthentication, before Build() - required, and
                    // never dialled: no test here presents a Keycloak token.
                    ["Operator__Authority"] = "https://keycloak.invalid/realms/ago-calendar",
                },
            },
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeout = new CancellationTokenSource(killAfter ?? ProcessTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on the paths where the host is supposed to keep running: this test has
                // already read what it came for out of the log.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        finally
        {
            process.CancelOutputRead();
            process.CancelErrorRead();
        }

        lock (output)
        {
            return new ProcessRun(process.ExitCode, output.ToString());
        }
    }

    private static async Task<bool> AnythingListeningAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
