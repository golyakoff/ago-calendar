using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.TenantScopeDiagnostics;
using Xunit.Abstractions;

namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// `24-17`: proves the runtime half (<see cref="CalendarTenantScopeInspector"/>, reached in
/// production through `Ago.Calendar.Api`'s `GET /api/v1/owner/tenant-isolation`) reports the same
/// figures a direct <see cref="CalendarTenantScopeRule.Scan"/> of the same, currently-built
/// `Ago.Calendar.Application.dll` produces - the calendar-side equivalent of
/// `Ago.Chat.Architecture.Tests.TenantScopeInspectorTests`.
///
/// <para><b>No build-time gate exists for this product to agree with.</b> Unlike `ago-chat`'s
/// `TenantScopeTests`, nothing in `ago-calendar`'s own build fails today if a handler is ungated -
/// `CalendarTenantScopeSnapshot`'s own remarks say why this item did not add one (inventing the
/// reasoned exemption catalogue that gate would need to enforce is a judgment call for a person, not
/// this item's job). What this test proves instead is narrower and still real: the two call sites this
/// item actually built - the static rule and the runtime port over it - cannot silently drift from each
/// other, because they call the identical <see cref="CalendarTenantScopeRule.Scan"/>.</para>
/// </summary>
public class CalendarTenantScopeInspectorTests(ITestOutputHelper output)
{
    [Fact]
    public void Snapshot_AgreesWithTheDirectScan_OnEveryFigure()
    {
        var direct = CalendarTenantScopeRule.Scan(TestAssemblies.Application.Cecil);
        var directGated = direct.Where(e => e.IsRbacGated).ToList();
        var directNotGated = direct.Where(e => !e.IsRbacGated).Select(e => e.Key).ToList();

        var snapshot = CalendarTenantScopeInspector.Scan(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Ago.Calendar.Application.dll"));

        // `24-17`'s own report needs the live numbers, printed once here rather than hand-copied into
        // docs/architecture/tenant-isolation.md and left to go stale.
        output.WriteLine($"EntryPoints={snapshot.EntryPoints} HandlerClasses={snapshot.HandlerClasses} "
            + $"RbacGated={snapshot.RbacGated} NotGated={snapshot.NotGated}");

        Assert.Equal(direct.Count, snapshot.EntryPoints);
        Assert.Equal(directGated.Count, snapshot.RbacGated);
        Assert.Equal(directNotGated.Count, snapshot.NotGated);
        Assert.Equal(
            directNotGated.OrderBy(k => k, StringComparer.Ordinal),
            snapshot.NotGatedKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>The fails-before this item's own brief asks for: point
    /// <see cref="CalendarTenantScopeInspector.Scan"/> at this test assembly's own compiled output,
    /// which carries <see cref="ForgetfulTenantScopedHandler"/> below - a handler that takes a
    /// `TenantId` and never calls `IPermissionChecker`. `NotGatedKeys` must include it.</summary>
    [Fact]
    public void Snapshot_FlagsAnUngatedHandler_WhenScanningAnAssemblyThatHasOne()
    {
        var snapshot = CalendarTenantScopeInspector.Scan(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Ago.Calendar.Architecture.Tests.dll"));

        Assert.Contains(
            "Ago.Calendar.Architecture.Tests.ForgetfulTenantScopedHandler.HandleAsync",
            snapshot.NotGatedKeys);
    }
}

/// <summary>The deliberate violation <see cref="CalendarTenantScopeInspectorTests.Snapshot_FlagsAnUngatedHandler_WhenScanningAnAssemblyThatHasOne"/>
/// scans for - structurally the same shape `Ago.Chat.Architecture.Tests.Fixtures.ForgetfulTenantScopedHandler`
/// is for `ago-chat`'s own rule: a command carrying a `TenantId`, a handler that never asks
/// `IPermissionChecker` about it. Nothing here is called at runtime; its IL is the whole point.</summary>
internal sealed record DoSomethingToATenant(TenantId TenantId);

internal sealed class ForgetfulTenantScopedHandler
{
    public Task<bool> HandleAsync(DoSomethingToATenant command, CancellationToken cancellationToken) =>
        Task.FromResult(command.TenantId != default);
}
