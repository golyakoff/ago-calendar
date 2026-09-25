using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage26AddEventPersonAndOriginConversation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "origin_conversation_id",
            table: "events",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "person_id",
            table: "events",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "origin_conversation_id",
            table: "events");

        migrationBuilder.DropColumn(
            name: "person_id",
            table: "events");
    }
}
