namespace Ago.Calendar.Domain.Tests;

public class TimeSlotTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WhenTheIntervalIsInverted_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSlot(Start, Start.AddMinutes(-1)));
    }

    [Fact]
    public void Constructor_WhenTheIntervalIsEmpty_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeSlot(Start, Start));
    }

    [Fact]
    public void Overlaps_ForBackToBackSlots_IsFalse()
    {
        // The half-open bound, and the reason for it: without this, every adjacent pair a
        // materialisation produces would be rejected by the storage-level no-overlap constraint,
        // which is declared with the identical '[)' bound.
        var first = new TimeSlot(Start, Start.AddMinutes(45));
        var second = new TimeSlot(Start.AddMinutes(45), Start.AddMinutes(90));

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
    }

    [Fact]
    public void Overlaps_ForAPartialCollision_IsTrue()
    {
        var first = new TimeSlot(Start, Start.AddMinutes(45));
        var second = new TimeSlot(Start.AddMinutes(30), Start.AddMinutes(75));

        Assert.True(first.Overlaps(second));
        Assert.True(second.Overlaps(first));
    }

    [Fact]
    public void Overlaps_ForAContainedSlot_IsTrue()
    {
        var outer = new TimeSlot(Start, Start.AddHours(3));
        var inner = new TimeSlot(Start.AddHours(1), Start.AddHours(2));

        Assert.True(outer.Overlaps(inner));
        Assert.True(inner.Overlaps(outer));
    }
}

public class PhoneNumberTests
{
    [Theory]
    [InlineData("+7 (999) 123-45-67", "+79991234567")]
    [InlineData("+79991234567", "+79991234567")]
    [InlineData("7 999 123 45 67", "+79991234567")]
    public void Constructor_NormalisesToE164(string input, string expected)
    {
        // Two customers or one? The unique index compares bytes, so the answer has to be settled
        // before the value reaches the column.
        Assert.Equal(expected, new PhoneNumber(input).Value);
        Assert.Equal(new PhoneNumber(expected), new PhoneNumber(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("09991234567")]
    [InlineData("+7999abc4567")]
    [InlineData("+7999123456789012345")]
    public void Constructor_RejectsWhatIsNotAPhoneNumber(string input)
    {
        Assert.Throws<ArgumentException>(() => new PhoneNumber(input));
    }
}

public class CalendarTimeZoneTests
{
    [Fact]
    public void Constructor_AcceptsAnIanaZoneId()
    {
        Assert.Equal("Europe/Moscow", new CalendarTimeZone("Europe/Moscow").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+03:00")]
    [InlineData("-05:00")]
    public void Constructor_RejectsEmptyValuesAndOffsets(string input)
    {
        Assert.Throws<ArgumentException>(() => new CalendarTimeZone(input));
    }
}

public class ServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Create_WithANonPositiveDuration_Throws(int minutes)
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Throws<ArgumentOutOfRangeException>(() => Service.Create(
            new ServiceId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id, "Haircut",
            TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Create_WithAFractionalMinute_Throws()
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.Throws<ArgumentOutOfRangeException>(() => Service.Create(
            new ServiceId(Guid.CreateVersion7(CalendarFixtures.Now)), tenant.Id, "Haircut",
            TimeSpan.FromSeconds(90)));
    }
}

public class CustomerTests
{
    [Fact]
    public void Touch_NeverMovesLastSeenBackwards()
    {
        var tenant = CalendarFixtures.Tenant();
        var customer = CalendarFixtures.Customer(tenant);
        var later = CalendarFixtures.Now.AddHours(1);
        customer.Touch(later);

        customer.Touch(CalendarFixtures.Now.AddMinutes(-30));

        Assert.Equal(later, customer.LastSeenAt);
    }

    [Fact]
    public void Describe_WithBlankValues_ClearsTheField()
    {
        var tenant = CalendarFixtures.Tenant();
        var customer = CalendarFixtures.Customer(tenant);
        customer.Describe("Ivan", "prefers mornings");

        customer.Describe("   ", null);

        Assert.Null(customer.DisplayName);
        Assert.Null(customer.Notes);
    }
}

// `22-05`/`adr/0093`: RoleTests removed - Role and Operator are gone along with the `roles`/
// `operators` tables they used to back. OperatorIdTests (this directory) covers what replaced them:
// OperatorId.FromExternalSubjectId's own determinism.
