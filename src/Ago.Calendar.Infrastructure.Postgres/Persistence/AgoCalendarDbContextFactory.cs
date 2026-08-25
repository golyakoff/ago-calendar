using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

/// <summary>
/// Design-time only - <c>dotnet ef migrations add</c>/<c>database update</c> need a way to construct
/// <see cref="AgoCalendarDbContext"/> without a running host's DI container. <c>migrations add</c>
/// never connects (schema generation is static from the model), so any syntactically valid
/// connection string satisfies it; <c>database update</c> needs a real one. Either way the value
/// comes from an environment variable and never a literal here - even a throwaway local placeholder
/// is a credential shape this repository does not commit (repositories.md: "no secrets, ever").
/// </summary>
public sealed class AgoCalendarDbContextFactory : IDesignTimeDbContextFactory<AgoCalendarDbContext>
{
    public AgoCalendarDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("AGO_CALENDAR_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set AGO_CALENDAR_CONNECTION_STRING before running dotnet ef - e.g. " +
                "Host=localhost;Port=5432;Database=ago_calendar;Username=...;Password=...");

        var optionsBuilder = new DbContextOptionsBuilder<AgoCalendarDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        return new AgoCalendarDbContext(optionsBuilder.Options);
    }
}
