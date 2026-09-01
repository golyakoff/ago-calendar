using Ago.Calendar.Api.Auth;
using Ago.Calendar.Api.Booking;
using Ago.Calendar.Api.ChatModule;
using Ago.Calendar.Api.Configuration;
using Ago.Calendar.Api.Cors;
using Ago.Calendar.Api.PhoneVerification;
using Ago.Calendar.Api.Provisioning;
using Ago.Calendar.Module;
using Ago.Platform.Hosting;
using Microsoft.AspNetCore.Cors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The composition root, and the only place that knows a concrete implementation
// (clean-architecture.md). AddPlatformKernel brings IClock/IIdGenerator in from the published
// package; the module registers this product's own adapters, handlers and options.
builder.Services.AddPlatformKernel();

IProductModule module = new CalendarModule();
module.ConfigureServices(builder.Services, builder.Configuration);

// `20-06`: adr/0022's OIDC scheme, this product's own copy (adr/0027). Host-level, not module-level,
// and that is the layering rather than a convenience: authentication is how *this deployable* decides
// who is calling, and Ago.Calendar.Worker - which loads the same module - has no callers at all.
builder.Services.AddCalendarOperatorAuthentication(builder.Configuration);

// `20-06`, layer 1 of `5-01`'s two-layer CORS model. AddCors registers the middleware's own default
// provider; replacing it afterwards is what makes every policy decision go through the database
// instead of through a static configuration.
builder.Services.AddCors();
builder.Services.AddSingleton(_ => new ConsoleOrigins(
    builder.Configuration.GetSection(ConsoleOrigins.SectionKey).Get<string[]>() ?? []));
builder.Services.AddSingleton<ICorsPolicyProvider, TenantOriginCorsPolicyProvider>();

var app = builder.Build();

// Before authentication, and that ordering is load-bearing: a preflight is an unauthenticated
// OPTIONS request that carries no token, so a CORS middleware sitting behind authentication would
// never answer one.
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// `20-03`: the product's first public write surface.
app.MapBookingEndpoints();

// `20-10`: the public widget's own phone-verification round trip - unlocks the booking endpoint above,
// which now requires it (BookEvent.RequiresVerifiedPhone).
app.MapPhoneVerificationEndpoints();

// `20-06`: the unauthenticated reads an embed makes, and the authenticated console behind adr/0022.
app.MapPublicBookingEndpoints();
app.MapConsoleEndpoints();

// `20-07`: the wire contract Ago.Chat.* drives a chat-originated booking through. Server-to-server,
// outside TenantOriginCorsPolicyProvider's two layers - see ChatModuleTaskEndpoints's own remarks.
app.MapChatModuleTaskEndpoints();

// Outside Production only - see DevProvisioningEndpoints for why the gate is the environment.
if (!app.Environment.IsProduction())
{
    app.MapDevProvisioningEndpoints();

    // `20-10`: read back the code FakePhoneVerificationSender captured, for a person clicking through
    // the widget by hand - see PhoneVerificationDevEndpoints's own remarks.
    app.MapPhoneVerificationDevEndpoints();
}

// Still here, and still earning its place: it answers with the loaded module's name rather than a
// constant, so "the host composed the module" is something the running process can be asked.
app.MapGet("/", () => module.Name);

app.Run();

/// <summary>
/// Named so that <c>Ago.Calendar.Integration.Tests</c> can point a <c>WebApplicationFactory</c> at
/// this host. A top-level-statements program's generated entry class is <c>internal</c>, and the
/// factory needs a public type argument; this partial declaration is the documented way to widen it
/// without turning the file into a conventional <c>Main</c>.
/// </summary>
public partial class Program;
