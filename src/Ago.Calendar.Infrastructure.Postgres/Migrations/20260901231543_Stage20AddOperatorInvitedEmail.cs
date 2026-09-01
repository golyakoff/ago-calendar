using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage20AddOperatorInvitedEmail : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "invited_email",
            table: "operators",
            type: "character varying(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_operators_invited_email",
            table: "operators",
            column: "invited_email");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_operators_invited_email",
            table: "operators");

        migrationBuilder.DropColumn(
            name: "invited_email",
            table: "operators");
    }
}
