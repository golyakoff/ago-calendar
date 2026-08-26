namespace Ago.Calendar.Architecture.Tests;

/// <summary>
/// adr/0049's central claim, made enforceable: <b>wall clock becomes an instant in exactly one
/// place.</b>
///
/// <para>The claim is easy to write in a doc comment and impossible to keep there. A second
/// <c>TimeZoneInfo.FindSystemTimeZoneById</c> anywhere - in a handler that needed "today", in an
/// endpoint rendering a slot for a customer, in a report - would reintroduce the class of bug this
/// whole time model exists to prevent, and it would compile, pass every other test, and only be
/// wrong twice a year. So the rule is asserted on assembly metadata, where a new call site cannot
/// hide.</para>
///
/// <para><c>Ago.Calendar.Infrastructure.Time</c> exists as its own assembly largely to make this
/// test expressible. A static helper inside Application would have been fewer files and there would
/// have been nothing to assert.</para>
/// </summary>
public class TimeZoneIsolationTests
{
    [Fact]
    public void TimeZoneInfo_IsUsedByExactlyOneProductAssembly()
    {
        var users = TestAssemblies.EveryProductAssembly
            .Where(assembly => IlMemberScanner.ReferencesType(assembly.Cecil, "System.TimeZoneInfo"))
            .Select(assembly => assembly.Name)
            .ToList();

        Assert.Equal(["Ago.Calendar.Infrastructure.Time"], users);
    }

    [Fact]
    public void Domain_NeverResolvesATimeZone()
    {
        // The inner-layer half of the same rule, stated separately because it is the one a reviewer
        // is most likely to break by accident: CalendarTimeZone validates the *shape* of a zone id
        // and it is tempting to make it validate existence too, which would make constructing a
        // calendar succeed or fail depending on which machine ran the code.
        var offenders = IlMemberScanner.FindCallers(
            TestAssemblies.Domain.Cecil, "System.TimeZoneInfo", "FindSystemTimeZoneById");

        Assert.True(offenders.Count == 0,
            $"Ago.Calendar.Domain resolves a time zone - that is Infrastructure's job (adr/0049). Callers: {string.Join(", ", offenders)}");
    }
}
