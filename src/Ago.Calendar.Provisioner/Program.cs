using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Provisioner;

// `ago-root#363`: the one-shot admin path chosen over a public signup endpoint and over a
// Keycloak-admin-API service account - see this item's own report for the argument. Argument parsing
// and a connection string; everything else is ProvisionerRunner, which a test drives against a real
// Postgres. No generic host, no DI container, no HTTP surface at all: this process reads five
// environment variables, writes one tenant, says what it did, and exits - the same shape
// Ago.Calendar.Migrator already established for "a process whose value is that it does one thing".

var connectionString = Environment.GetEnvironmentVariable("AGO_CALENDAR_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync(
        "Set AGO_CALENDAR_CONNECTION_STRING - e.g. Host=localhost;Port=5432;Database=ago_calendar;"
        + "Username=...;Password=...");
    return ProvisionerRunner.Failure;
}

var tenantName = Environment.GetEnvironmentVariable("AGO_CALENDAR_TENANT_NAME");
var publicKey = Environment.GetEnvironmentVariable("AGO_CALENDAR_TENANT_PUBLIC_KEY");
var ownerDisplayName = Environment.GetEnvironmentVariable("AGO_CALENDAR_OWNER_DISPLAY_NAME");
// Deliberately the only identity this tool ever reads - no AGO_CALENDAR_OWNER_SUBJECT variable
// exists here, and none should be added. See ProvisionerRunner's own remarks: a real owner's
// Keycloak sub is never known at provisioning time, so a variable for it would only invite guessing
// one.
var ownerEmail = Environment.GetEnvironmentVariable("AGO_CALENDAR_OWNER_EMAIL");
var allowedOriginsRaw = Environment.GetEnvironmentVariable("AGO_CALENDAR_ALLOWED_ORIGINS");

var missing = new List<string>();
if (string.IsNullOrWhiteSpace(tenantName))
{
    missing.Add("AGO_CALENDAR_TENANT_NAME");
}

if (string.IsNullOrWhiteSpace(publicKey))
{
    missing.Add("AGO_CALENDAR_TENANT_PUBLIC_KEY");
}

if (string.IsNullOrWhiteSpace(ownerDisplayName))
{
    missing.Add("AGO_CALENDAR_OWNER_DISPLAY_NAME");
}

if (string.IsNullOrWhiteSpace(ownerEmail))
{
    missing.Add("AGO_CALENDAR_OWNER_EMAIL");
}

if (missing.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"Missing required variable(s): {string.Join(", ", missing)}. Usage: set "
        + "AGO_CALENDAR_CONNECTION_STRING, AGO_CALENDAR_TENANT_NAME, AGO_CALENDAR_TENANT_PUBLIC_KEY, "
        + "AGO_CALENDAR_OWNER_DISPLAY_NAME, AGO_CALENDAR_OWNER_EMAIL and, optionally, "
        + "AGO_CALENDAR_ALLOWED_ORIGINS (comma-separated).");
    return ProvisionerRunner.Failure;
}

IReadOnlyList<string> allowedOrigins = string.IsNullOrWhiteSpace(allowedOriginsRaw)
    ? []
    : allowedOriginsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var command = new RegisterTenant(
    tenantName!, publicKey!, ownerDisplayName!, ExternalSubjectId: null, allowedOrigins, ownerEmail);

return await ProvisionerRunner.RunAsync(connectionString, command, Console.Out, CancellationToken.None);
