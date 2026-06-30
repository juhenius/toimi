using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.tietue.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggersAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_triggers",
                table: "type_definitions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "entity_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entity_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_entity_events_entities_entity_id",
                        column: x => x.entity_id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "triggers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule = table.Column<string>(type: "jsonb", nullable: false),
                    handler_kind = table.Column<string>(type: "text", nullable: false),
                    handler_config = table.Column<string>(type: "jsonb", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    next_fire_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_triggers", x => x.id);
                    table.ForeignKey(
                        name: "fk_triggers_entities_entity_id",
                        column: x => x.entity_id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entity_events_entity_id_occurrence_utc_kind",
                table: "entity_events",
                columns: new[] { "entity_id", "occurrence_utc", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_triggers_enabled_next_fire_at",
                table: "triggers",
                columns: new[] { "enabled", "next_fire_at" });

            migrationBuilder.CreateIndex(
                name: "ix_triggers_entity_id",
                table: "triggers",
                column: "entity_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_events");

            migrationBuilder.DropTable(
                name: "triggers");

            migrationBuilder.DropColumn(
                name: "default_triggers",
                table: "type_definitions");
        }
    }
}
