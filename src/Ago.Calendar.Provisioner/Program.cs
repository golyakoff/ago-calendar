using Ago.Calendar.Application.UseCases.Provisioning;
using Ago.Calendar.Domain;
using Ago.Calendar.Provisioner;

// `ago-root#363`: the one-shot admin path chosen over a public signup endpoint and over a
// Keycloak-admin-API service account - see this item's own report for the argument. Argument parsing
// and a connection string; everything else is ProvisionerRunner, which a test drives against a real
// Postgres. No generic host, no DI container, no HTTP surface at all: this process reads a handful of
// environment variables, writes one tenant, says what it did, and exits - the same shape
// Ago.Calendar.Migrator already established for "a process whose value is that it does one thing".
//
// `22-05`/`adr/0093`: AGO_CALENDAR_OWNER_DISPLAY_NAME and AGO_CALENDAR_OWNER_EMAIL are gone - this
// tool no longer writes an operator alongside the tenant (ProvisionerRunner's own remarks). The
// account owner's calendar access is a permission grant on the account side now, not something this
// tool provisions.

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
var allowedOriginsRaw = Environment.GetEnvironmentVariable("AGO_CALENDAR_ALLOWED_ORIGINS");
// `22-03`/adr/0093: optional, and the only variable `22-03` added, unchanged since. Set when the
// caller already has an account id - the chat side's own `SiteId` - so this run provisions the
// calendar's tenant *as* that account rather than minting a tenancy id of its own. Unset keeps the
// standalone door adr/0093 kept open: this tool still mints its own id, exactly as before that item.
var tenantIdRaw = Environment.GetEnvironmentVariable("AGO_CALENDAR_TENANT_ID");

var missing = new List<string>();
if (string.IsNullOrWhiteSpace(tenantName))
{
    missing.Add("AGO_CALENDAR_TENANT_NAME");
}

if (string.IsNullOrWhiteSpace(publicKey))
{
    missing.Add("AGO_CALENDAR_TENANT_PUBLIC_KEY");
}

if (missing.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"Missing required variable(s): {string.Join(", ", missing)}. Usage: set "
        + "AGO_CALENDAR_CONNECTION_STRING, AGO_CALENDAR_TENANT_NAME, AGO_CALENDAR_TENANT_PUBLIC_KEY and, "
        + "optionally, AGO_CALENDAR_ALLOWED_ORIGINS (comma-separated) and AGO_CALENDAR_TENANT_ID (the "
        + "account id, when this tenant is not standalone).");
    return ProvisionerRunner.Failure;
}

TenantId? tenantId = null;
if (!string.IsNullOrWhiteSpace(tenantIdRaw))
{
    if (!Guid.TryParse(tenantIdRaw, out var parsedTenantId))
    {
        await Console.Error.WriteLineAsync(
            $"AGO_CALENDAR_TENANT_ID is set but '{tenantIdRaw}' is not a GUID.");
        return ProvisionerRunner.Failure;
    }

    tenantId = new TenantId(parsedTenantId);
}

IReadOnlyList<string> allowedOrigins = string.IsNullOrWhiteSpace(allowedOriginsRaw)
    ? []
    : allowedOriginsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var command = new RegisterTenant(tenantName!, publicKey!, allowedOrigins, tenantId);

return await ProvisionerRunner.RunAsync(connectionString, command, Console.Out, CancellationToken.None);
