#pragma warning disable
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.muistutin.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifiedOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "notified_at",
                table: "reminders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notified_occurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    reminder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notified_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "fk_notified_occurrences_reminders_reminder_id",
                        column: x => x.reminder_id,
                        principalTable: "reminders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notified_occurrences_reminder_id_occurrence_utc",
                table: "notified_occurrences",
                columns: new[] { "reminder_id", "occurrence_utc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notified_occurrences");

            migrationBuilder.DropColumn(
                name: "notified_at",
                table: "reminders");
        }
    }
}
