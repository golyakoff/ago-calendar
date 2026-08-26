using Ago.Calendar.Api.Booking;
using Ago.Calendar.Module;
using Ago.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The composition root, and the only place that knows a concrete implementation
// (clean-architecture.md). AddPlatformKernel brings IClock/IIdGenerator in from the published
// package; the module registers this product's own adapters, handlers and options.
builder.Services.AddPlatformKernel();

IProductModule module = new CalendarModule();
module.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();

// `20-03`: the product's first real endpoint, and its only public write surface.
app.MapBookingEndpoints();

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
