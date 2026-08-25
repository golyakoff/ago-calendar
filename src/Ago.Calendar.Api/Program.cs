using Ago.Calendar.Module;
using Ago.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The composition root, and the only place that knows a concrete implementation
// (clean-architecture.md). AddPlatformKernel brings IClock/IIdGenerator in from the published
// package; the module registers this product's own services - nothing, until `20-01`.
builder.Services.AddPlatformKernel();

IProductModule module = new CalendarModule();
module.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();

// A placeholder, replaced by real endpoints in `20-01`. It answers with the loaded module's name
// rather than a constant so that "the host composed the module" is something the running process
// can actually be asked, not only something this file claims.
app.MapGet("/", () => module.Name);

app.Run();
