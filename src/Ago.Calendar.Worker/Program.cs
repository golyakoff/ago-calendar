using Ago.Calendar.Module;
using Ago.Calendar.Worker;
using Ago.Platform.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Same composition root as Ago.Calendar.Api, same module - the two hosts differ in what they run,
// never in what the product is (adr/0013).
builder.Services.AddPlatformKernel();

IProductModule module = new CalendarModule();
module.ConfigureServices(builder.Services, builder.Configuration);

// `20-02`: this host's first real work. The options binding and the AddHostedService call are the
// only genuinely host-shaped part of the slice - everything the job uses is registered by the module
// and is identical in Ago.Calendar.Api, which simply does not run it.
builder.Services
    .AddOptions<AvailabilityMaterializationJobOptions>()
    .Bind(builder.Configuration.GetSection(AvailabilityMaterializationJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AvailabilityMaterializationJob>();

// `20-04`: the other half of the two-step booking mechanic. Same host, same shape, same reasoning as
// the materialisation job above - and a much shorter interval, because this one's latency is a
// customer waiting to be told their booking is settled.
builder.Services
    .AddOptions<PendingBookingSweepJobOptions>()
    .Bind(builder.Configuration.GetSection(PendingBookingSweepJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<PendingBookingSweepJob>();

var host = builder.Build();
host.Run();
