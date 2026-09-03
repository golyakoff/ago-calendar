using Ago.Calendar.Infrastructure.Postgres.Schema;
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

// `22-05`/`adr/0093`: this product's first broker consumer - projects `ago-chat`'s own
// `RoleAssignmentsChanged` into the local `role_assignment_projections` table.
builder.Services
    .AddOptions<RoleAssignmentsChangedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(RoleAssignmentsChangedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<RoleAssignmentsChangedConsumer>();

var host = builder.Build();

// `20-21`/`adr/0056`: the same guard Ago.Calendar.Api runs, in the same place - before anything can
// run, and deliberately not as an IHostedService, for the identical ordering reason that host's own
// Program.cs records. This host has no listening socket, but it does have AvailabilityMaterializationJob
// and PendingBookingSweepJob, both registered as hosted services above and both about to run against
// this database; a job racing a schema it does not match is the same "quiet until a query touches the
// wrong column" failure as a request would be.
await host.Services.EnsureSchemaIsCurrentAsync();

host.Run();
