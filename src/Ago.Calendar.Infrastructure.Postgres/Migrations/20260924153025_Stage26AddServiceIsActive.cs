using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `26-96`: <c>services.is_active</c> - the flag that makes "take this service out of rotation" a
/// thing this product can do at all, and the reason it still has no <c>DELETE /services/{id}</c>
/// (see <c>Ago.Calendar.Domain.Service</c>'s own remarks).
///
/// <para><b>The default is <c>true</c>, and that is the whole backfill.</b> Every service that
/// existed before this column did was, by definition, on offer - there was no way to withdraw one.
/// Writing the default into the <c>ADD COLUMN</c> rather than following it with an
/// <c>UPDATE ... SET is_active = true</c> is what keeps this a single, non-blocking statement on
/// Postgres 11+ (the default is stored in the catalogue, not rewritten into every row) and what makes
/// the migration's own reversibility trivial: <see cref="Down"/> drops a column nothing else depends
/// on. <c>MigrationReversibilityTests</c> exercises exactly that.</para>
/// </summary>
/// <inheritdoc />
public partial class Stage26AddServiceIsActive : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "is_active",
            table: "services",
            type: "boolean",
            nullable: false,
            defaultValue: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "is_active",
            table: "services");
    }
}
