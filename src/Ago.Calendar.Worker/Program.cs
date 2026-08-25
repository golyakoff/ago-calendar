using Ago.Calendar.Module;
using Ago.Platform.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Same composition root as Ago.Calendar.Api, same module - the two hosts differ in what they run,
// never in what the product is (adr/0013).
builder.Services.AddPlatformKernel();

IProductModule module = new CalendarModule();
module.ConfigureServices(builder.Services, builder.Configuration);

// No IHostedService yet: `20-01` brings the first consumer and the outbox dispatcher. The generic
// host still blocks on its own lifetime, so this process starts and idles rather than exiting -
// deliberately, so that a placeholder background loop nobody wants is not committed just to have
// something running.
var host = builder.Build();
host.Run();
