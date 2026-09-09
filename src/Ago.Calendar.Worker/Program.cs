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

// `22-07`/`adr/0093`: this product's second broker consumer - applies `ago-chat`'s own granted
// worker quota (`ModuleQuantityGranted`) to the local tenancy row, same shape as the consumer above.
builder.Services
    .AddOptions<ModuleQuantityGrantedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ModuleQuantityGrantedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ModuleQuantityGrantedConsumer>();

// `23-12`/`adr/0123`: this product's third broker consumer - projects `ago-chat`'s own
// `ContactVisibilityChanged` into the local `contact_visibility_projections` table, same shape as
// the two consumers above.
builder.Services
    .AddOptions<ContactVisibilityChangedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ContactVisibilityChangedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ContactVisibilityChangedConsumer>();

// `23-12`'s own retention: prunes `contact_phone_reveals` past its configured window, the same
// bounded-batch-delete-on-a-schedule shape as `PendingBookingSweepJob`/`AvailabilityMaterializationJob`
// above.
builder.Services
    .AddOptions<ContactPhoneRevealPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(ContactPhoneRevealPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ContactPhoneRevealPruneJob>();

// `23-59`/`adr/0147`: this product's fourth broker consumer - projects `ago-chat`'s own
// `ContactCollected` into a `Customer` row, for a tenant that has this product provisioned. See
// ContactCollectedConsumer's own remarks for the local tenant-existence gate and the phone-kind filter.
builder.Services
    .AddOptions<ContactCollectedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ContactCollectedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ContactCollectedConsumer>();

// `23-88`/`adr/0165`: this product's fifth broker consumer, and the first that publishes back rather
// than only applying an incoming fact - answers chat's own async worker-quota impact question
// (`ModuleQuantityImpactRequested`) on this product's own outbox (`ModuleQuantityImpactComputed`),
// same registration shape as every consumer above.
builder.Services
    .AddOptions<ModuleQuantityImpactRequestedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ModuleQuantityImpactRequestedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ModuleQuantityImpactRequestedConsumer>();

var host = builder.Build();

// `20-21`/`adr/0056`: the same guard Ago.Calendar.Api runs, in the same place - before anything can
// run, and deliberately not as an IHostedService, for the identical ordering reason that host's own
// Program.cs records. This host has no listening socket, but it does have AvailabilityMaterializationJob
// and PendingBookingSweepJob, both registered as hosted services above and both about to run against
// this database; a job racing a schema it does not match is the same "quiet until a query touches the
// wrong column" failure as a request would be.
await host.Services.EnsureSchemaIsCurrentAsync();

host.Run();
