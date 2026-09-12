using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// <c>Ago.Calendar.Api</c> in-process, pointed at this suite's own containers.
///
/// <para>The connection strings arrive as configuration rather than as environment variables, which
/// is why <c>CalendarModule</c> reads configuration first as of `20-03`: setting a process-wide
/// variable from a test would leak into every other test in the assembly, and xunit runs collections
/// in parallel.</para>
///
/// <para><b>`20-06`: <c>Operator:Authority</c> is a required setting and this supplies a URL that
/// answers nothing.</b> That is deliberate and it costs nothing, because <c>AddJwtBearer</c> fetches
/// JWKS lazily, on the first token it is asked to validate - and no test here presents a Keycloak
/// token. What the setting proves by being required is the property `20-06` wants: this host has no
/// fallback authentication and no dev stub, so it cannot start with a trust model adr/0022
/// deleted.</para>
/// </summary>
internal class CalendarApiFactory(PostgresFixture fixture) : WebApplicationFactory<Program>
{
    /// <summary>A syntactically valid realm URL that resolves to nothing. Never contacted - see the
    /// type's own remarks.</summary>
    public const string UnreachableAuthority = "https://keycloak.invalid/realms/ago";

    /// <summary>`22-11`: the shared secret every suite using this factory can provision with -
    /// <see cref="ModuleRegistrationEndpointTests"/>'s own <c>X-Ago-Module-Provisioning-Secret</c>
    /// header. Set here rather than per-suite, the same "one wide-open value every consumer of this
    /// factory can rely on" shape the <c>BookingRateLimit</c> settings below already use, since no
    /// other suite in this project has any reason to send a competing value.</summary>
    public const string TestProvisioningSecret = "integration-test-provisioning-secret-of-sufficient-length";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:Calendar", fixture.ConnectionString);
        builder.UseSetting("Redis:ConnectionString", fixture.RedisConnectionString);
        builder.UseSetting("Operator:Authority", UnreachableAuthority);
        builder.UseSetting("ModuleProvisioning:Secret", TestProvisioningSecret);

        // `22-05`/`adr/0093` named this setting for AddRabbitMqMessaging's own ValidateOnStart, back
        // when nothing in Ago.Calendar.Api actually dialled the broker (RoleAssignmentsChangedConsumer
        // is Ago.Calendar.Worker-only) - a syntactically valid, never-dialled "rabbitmq.invalid" value
        // satisfied validation with no broker container in this fixture at all.
        //
        // `25-63`: that stopped being true. CalendarOperatorHub's own host-side wiring registers
        // NodeDeliveryConsumer as a hosted service, and that service genuinely subscribes on this
        // host's own startup - the first real dial. A fake host now fails fast enough that
        // BackgroundService.StartAsync propagates it into the whole host's own StartAsync, which
        // WebApplicationFactory surfaces as a torn-down IServiceProvider on every later call, not as
        // a clean startup failure - found live running this project's own real HTTP suite, not
        // designed in. Points at PostgresFixture's own real RabbitMQ container instead
        // (see that fixture's own remarks on why it now has one).
        builder.UseSetting("Messaging:RabbitMq:HostName", fixture.RabbitMq.Hostname);
        builder.UseSetting("Messaging:RabbitMq:Port", fixture.RabbitMq.GetMappedPublicPort(5672).ToString());
        builder.UseSetting("Messaging:RabbitMq:UserName", "ago-test");
        builder.UseSetting("Messaging:RabbitMq:Password", "ago-test-local-dev");

        // Wide open, so a rate limit never interferes with a test that is about something else. The
        // limiter's own behaviour is proved separately, against a real Redis, in
        // Ago.Calendar.Concurrency.Tests.
        builder.UseSetting("BookingRateLimit:PerPhoneCapacity", "100000");
        builder.UseSetting("BookingRateLimit:PerPhoneRefillPerSecond", "100000");
        builder.UseSetting("BookingRateLimit:PerCalendarCapacity", "100000");
        builder.UseSetting("BookingRateLimit:PerCalendarRefillPerSecond", "100000");
    }
}

/// <summary>
/// The same host with a stand-in for Keycloak: a scheme that turns an <c>X-Test-Subject</c> header
/// into an authenticated principal carrying that <c>sub</c>, and nothing else.
///
/// <para><b>What this does and does not fake, which is the whole reason it is shaped this way.</b> It
/// replaces exactly one thing - proof that Keycloak signed a token for a subject - and leaves the
/// step this item actually built running for real:
/// <c>OperatorIdentityClaimsTransformation</c> still resolves that <c>sub</c> against the real
/// <c>role_assignment_projections</c> table on a real Postgres (`22-05`/`adr/0093`: no
/// <c>operators</c> table exists any more), the <c>calendar-operator</c> policy still refuses a
/// principal it fails to resolve, and every handler still checks a real permission through the real
/// <c>PermissionChecker</c>. A fake that minted <c>operator_id</c> directly would have skipped all
/// three and proved nothing but that the fake works.</para>
///
/// <para>Running a real Keycloak container for this would prove that <c>AddJwtBearer</c> validates a
/// signature, which is framework code this project does not re-test - the same call `5-01` made about
/// the CORS middleware.</para>
/// </summary>
internal sealed class ConsoleApiFactory(PostgresFixture fixture) : CalendarApiFactory(fixture)
{
    public const string SubjectHeader = "X-Test-Subject";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            // PostConfigure rather than a second AddAuthentication: the host has already named the
            // JWT scheme as the default, and re-registering would leave two defaults with the last
            // writer winning silently.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, HeaderSubjectAuthenticationHandler>(
                    HeaderSubjectAuthenticationHandler.SchemeName, _ => { });

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = HeaderSubjectAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = HeaderSubjectAuthenticationHandler.SchemeName;
            });
        });
    }
}

/// <summary>Authenticates whoever the <c>X-Test-Subject</c> header names, with a <c>sub</c> claim and
/// nothing else. `22-05`/`adr/0093`: the <c>X-Test-Email</c> header and the <c>email</c> claim it used
/// to add are gone - <c>OperatorIdentityClaimsTransformation</c> reads only <c>sub</c> now, since
/// adr/0088's email fallback (the only reader of an email claim) was retired along with the rest of
/// this product's own identity model.</summary>
internal sealed class HeaderSubjectAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestSubject";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers[ConsoleApiFactory.SubjectHeader].ToString();
        if (string.IsNullOrWhiteSpace(subject))
        {
            // NoResult, not Fail: "nobody presented anything" is an anonymous request, and the
            // authorization policy is what turns that into a 401 on a protected route.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim("sub", subject)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
