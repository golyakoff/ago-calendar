namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `20-06`: the tenant's public embed surface - the key a script tag names it by, and the origins
/// allowed to use it.
///
/// <para>Everything here is a rule about *which strings are the same origin*, which is exactly the
/// kind of rule that is quietly wrong in three places when each caller compares strings its own way.
/// The aggregate owning the comparison is what these tests are really pinning down.</para>
/// </summary>
public class TenantEmbedSurfaceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("Barbershop")]
    [InlineData("shop key")]
    [InlineData("shop.key")]
    [InlineData("shop/key")]
    public void APublicKey_RefusesAnythingThatIsNotLowercaseUrlSafeAndLongEnough(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new TenantPublicKey(value));

    [Fact]
    public void APublicKey_RefusesAValueLongerThanTheColumn() =>
        Assert.ThrowsAny<ArgumentException>(
            () => new TenantPublicKey(new string('a', TenantPublicKey.MaxLength + 1)));

    [Theory]
    [InlineData("abc")]
    [InlineData("demo-shop_2")]
    public void APublicKey_AcceptsLowercaseUrlSafeValues(string value) =>
        Assert.Equal(value, new TenantPublicKey(value).Value);

    [Fact]
    public void ANewTenant_AllowsNoOriginAtAll()
    {
        // The safe default, and the one a shop notices: nobody's page may embed this tenant until
        // somebody says one may. The opposite default would be a tenant every page can embed.
        var tenant = CalendarFixtures.Tenant();

        Assert.Empty(tenant.AllowedOrigins);
        Assert.False(tenant.Allows("https://shop.example"));
    }

    [Theory]
    [InlineData("https://shop.example", "https://shop.example")]
    [InlineData("https://shop.example/", "https://shop.example")]
    [InlineData("HTTPS://Shop.Example", "https://shop.example")]
    [InlineData("  https://shop.example  ", "https://shop.example")]
    [InlineData("http://localhost:8095", "http://localhost:8095")]
    public void AnOrigin_IsNormalisedOnTheWayIn(string typed, string stored)
    {
        var tenant = CalendarFixtures.Tenant(allowedOrigins: [typed]);

        Assert.Equal([stored], tenant.AllowedOrigins);
    }

    [Fact]
    public void AnOrigin_MatchesRegardlessOfHowTheHeaderIsCased()
    {
        // A browser sends a lowercase scheme and host already; what this pins down is that a *human*
        // typing it into the console cannot create an entry that never matches.
        var tenant = CalendarFixtures.Tenant(allowedOrigins: ["HTTPS://Shop.Example/"]);

        Assert.True(tenant.Allows("https://shop.example"));
        Assert.True(tenant.Allows("https://SHOP.example"));
    }

    [Theory]
    [InlineData("https://shop.example/booking")]
    [InlineData("https://shop.example?a=b")]
    [InlineData("https://shop.example#x")]
    [InlineData("shop.example")]
    [InlineData("not an origin")]
    public void AnOrigin_WithAPathQueryFragmentOrNoScheme_IsRefusedRatherThanTrimmed(string value) =>
        // Trimming would grant more than the tenant asked for: somebody who typed a path believes the
        // path is doing something. Refusing tells them it is not.
        Assert.Throws<ArgumentException>(() => CalendarFixtures.Tenant(allowedOrigins: [value]));

    [Fact]
    public void AnotherTenantsApprovedOrigin_IsStillNotThisTenantsOrigin()
    {
        // The whole point of layer 2. Layer 1 would say yes to both of these, because *some* tenant
        // allows each - which is precisely why layer 1 is not a tenant boundary.
        var a = CalendarFixtures.Tenant(publicKey: "shop-a", allowedOrigins: ["https://a.example"]);
        var b = CalendarFixtures.Tenant(publicKey: "shop-b", allowedOrigins: ["https://b.example"]);

        Assert.True(a.Allows("https://a.example"));
        Assert.False(a.Allows("https://b.example"));
        Assert.True(b.Allows("https://b.example"));
        Assert.False(b.Allows("https://a.example"));
    }

    [Fact]
    public void SettingOrigins_ReplacesTheListRatherThanAddingToIt()
    {
        var tenant = CalendarFixtures.Tenant(allowedOrigins: ["https://old.example"]);

        tenant.SetAllowedOrigins(["https://new.example"]);

        Assert.Equal(["https://new.example"], tenant.AllowedOrigins);
        Assert.False(tenant.Allows("https://old.example"));
    }

    [Fact]
    public void SettingOrigins_KeepsOneCopyOfADuplicate()
    {
        var tenant = CalendarFixtures.Tenant();

        tenant.SetAllowedOrigins(["https://shop.example", "https://shop.example/", "HTTPS://SHOP.EXAMPLE"]);

        Assert.Equal(["https://shop.example"], tenant.AllowedOrigins);
    }

    [Fact]
    public void AnEmptyOriginString_IsNeverAllowed()
    {
        // A request with no Origin header reaches the aggregate as an empty string on some paths.
        // "Allows nothing" is the right answer here; whether an *absent* header is a rejection is a
        // separate decision, and it is OriginPolicy's, not this aggregate's.
        var tenant = CalendarFixtures.Tenant(allowedOrigins: ["https://shop.example"]);

        Assert.False(tenant.Allows(string.Empty));
        Assert.False(tenant.Allows("   "));
    }

    // ------------------------------------------------------------------------------------------
    // `22-17`: the provenance marker - a human-registered tenant (Register, every path above this
    // point) is never auto-provisioned, and the one path that is auto-provisioned
    // (AutoProvisionForChatModule) never produces anything else.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void ATenantRegisteredTheOrdinaryWay_IsNeverAutoProvisioned()
    {
        var tenant = CalendarFixtures.Tenant();

        Assert.False(tenant.AutoProvisioned);
    }

    [Fact]
    public void AutoProvisionForChatModule_ProducesATenant_MarkedAutoProvisioned()
    {
        var id = new TenantId(Guid.NewGuid());
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        var tenant = Tenant.AutoProvisionForChatModule(id, "A Brand New Prospect", new TenantPublicKey("chat-abc123"), now);

        Assert.True(tenant.AutoProvisioned);
        Assert.Equal("A Brand New Prospect", tenant.Name);
        Assert.Equal(id, tenant.Id);
        Assert.Empty(tenant.AllowedOrigins);
    }

    [Fact]
    public void AutoProvisionForChatModule_WithABlankName_Throws() =>
        Assert.ThrowsAny<ArgumentException>(() => Tenant.AutoProvisionForChatModule(
            new TenantId(Guid.NewGuid()), "   ", new TenantPublicKey("chat-abc123"), DateTimeOffset.UtcNow));
}
