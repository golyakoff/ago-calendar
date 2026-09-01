namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `adr/0088`: <see cref="InvitedEmail"/>'s own shape rules, and the invited shape of
/// <see cref="Operator.Create"/> - no database here, the same reasoning
/// <see cref="OperatorAccountOwnerTests"/>' own remarks give for staying at this level.
/// </summary>
public class OperatorInviteTests
{
    private static readonly DateTimeOffset Now = CalendarFixtures.Now;

    [Theory]
    [InlineData("Alex@Shop.example", "alex@shop.example")]
    [InlineData("  robin@example.com  ", "robin@example.com")]
    public void InvitedEmail_Normalises_TrimmedAndLowercased(string input, string expected)
    {
        var email = new InvitedEmail(input);

        Assert.Equal(expected, email.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("robin@")]
    [InlineData("robin@@example.com")]
    public void InvitedEmail_RejectsAnythingNotShapedLikeOneAddress(string input)
    {
        Assert.Throws<ArgumentException>(() => new InvitedEmail(input));
    }

    [Fact]
    public void TwoInvitedEmails_WithDifferentCasing_AreEqual()
    {
        // Value equality on the normalised form is what lets a repository compare them directly -
        // the same property PhoneNumber's own normalisation gives customer lookups.
        Assert.Equal(new InvitedEmail("Robin@Example.com"), new InvitedEmail("robin@example.com"));
    }

    [Fact]
    public void Create_WithAnInvitedEmail_ProducesAnUnlinkedNonOwnerOperator()
    {
        var tenant = CalendarFixtures.Tenant();

        var invited = Operator.Create(
            new OperatorId(NewId()), tenant.Id, "Robin", invitedEmail: new InvitedEmail("robin@example.com"));

        Assert.Null(invited.ExternalSubjectId);
        Assert.False(invited.IsAccountOwner);
        Assert.Equal("robin@example.com", invited.InvitedEmail!.Value.Value);
        Assert.Empty(invited.Roles);
    }

    [Fact]
    public void Create_WithNoInvitedEmail_LeavesItNull()
    {
        // The account owner's own shape - RegisterTenantHandler never passes one.
        var tenant = CalendarFixtures.Tenant();

        var owner = Operator.Create(new OperatorId(NewId()), tenant.Id, "Sam", isAccountOwner: true);

        Assert.Null(owner.InvitedEmail);
    }

    [Fact]
    public void LinkExternalIdentity_OnAnInvitedOperator_LeavesInvitedEmailInPlace()
    {
        // adr/0088: the field is a historical record of who the invite was for, not a live lookup key
        // that needs clearing once it has done its job.
        var tenant = CalendarFixtures.Tenant();
        var invited = Operator.Create(
            new OperatorId(NewId()), tenant.Id, "Robin", invitedEmail: new InvitedEmail("robin@example.com"));

        invited.LinkExternalIdentity("kc-robin");

        Assert.Equal("kc-robin", invited.ExternalSubjectId);
        Assert.Equal("robin@example.com", invited.InvitedEmail!.Value.Value);
    }

    [Fact]
    public void ANonOwnerInvitedOperator_CanBeGrantedARoleWithNoCustomerRead_WithoutTheAccountOwnerInvariantFiring()
    {
        // The known trap named in this item's own brief: 20-12's account-owner invariant must never
        // fire for an invited (non-owner) operator.
        var tenant = CalendarFixtures.Tenant();
        var dispatcher = Role.Create(
            new RoleId(NewId()), tenant.Id, "Dispatcher", [Permission.BookingReject, Permission.BookingCancel]);
        var invited = Operator.Create(
            new OperatorId(NewId()), tenant.Id, "Robin", invitedEmail: new InvitedEmail("robin@example.com"));

        invited.Grant(dispatcher);

        Assert.Contains(invited.Roles, a => a.RoleId == dispatcher.Id);
    }

    private static Guid NewId() => Guid.CreateVersion7(Now);
}
