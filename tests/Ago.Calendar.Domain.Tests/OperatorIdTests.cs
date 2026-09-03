using Ago.Calendar.Domain;

namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `22-05`/`adr/0093`: <see cref="OperatorId.FromExternalSubjectId"/> is what replaced a
/// database-assigned <c>operators.id</c> - a pure function, so its whole contract is determinism and
/// non-collision, both provable with no database at all.
/// </summary>
public class OperatorIdTests
{
    [Fact]
    public void FromExternalSubjectId_IsDeterministic_TheSameSubjectAlwaysDerivesTheSameId()
    {
        var first = OperatorId.FromExternalSubjectId("keycloak-sub-1");
        var second = OperatorId.FromExternalSubjectId("keycloak-sub-1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void FromExternalSubjectId_DifferentSubjects_DeriveDifferentIds()
    {
        var a = OperatorId.FromExternalSubjectId("keycloak-sub-a");
        var b = OperatorId.FromExternalSubjectId("keycloak-sub-b");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FromExternalSubjectId_NeverProducesTheEmptyGuid()
    {
        // A cheap sanity check that the version/variant bit-twiddling did not accidentally zero the
        // whole value for some input.
        var id = OperatorId.FromExternalSubjectId("keycloak-sub-1");

        Assert.NotEqual(default, id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromExternalSubjectId_RejectsBlank(string subject)
    {
        Assert.Throws<ArgumentException>(() => OperatorId.FromExternalSubjectId(subject));
    }
}
