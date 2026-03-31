using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.muistutin.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    date_time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    recurrence_rule = table.Column<string>(type: "text", nullable: true),
                    display_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_completed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reminders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "completed_occurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    reminder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_completed_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "fk_completed_occurrences_reminders_reminder_id",
                        column: x => x.reminder_id,
                        principalTable: "reminders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_completed_occurrences_reminder_id_occurrence_utc",
                table: "completed_occurrences",
                columns: new[] { "reminder_id", "occurrence_utc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_reminders_active_range",
                table: "reminders",
                columns: new[] { "date_time_utc", "display_end_utc" },
                filter: "NOT is_completed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "completed_occurrences");

            migrationBuilder.DropTable(
                name: "reminders");
        }
    }
}
