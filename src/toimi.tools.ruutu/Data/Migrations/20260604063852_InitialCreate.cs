using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace toimi.tools.ruutu.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "displays",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    identifier = table.Column<string>(type: "text", nullable: false),
                    tier = table.Column<string>(type: "text", nullable: true),
                    tier_override = table.Column<bool>(type: "boolean", nullable: false),
                    last_user_agent = table.Column<string>(type: "text", nullable: true),
                    viewport_width = table.Column<int>(type: "integer", nullable: true),
                    viewport_height = table.Column<int>(type: "integer", nullable: true),
                    orientation = table.Column<string>(type: "text", nullable: true),
                    current_template = table.Column<string>(type: "text", nullable: true),
                    current_data = table.Column<string>(type: "jsonb", nullable: true),
                    current_pushed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    overlay_stack = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    idle_template = table.Column<string>(type: "text", nullable: true),
                    idle_data = table.Column<string>(type: "jsonb", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_displays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    schema_json = table.Column<string>(type: "jsonb", nullable: false),
                    modern_html = table.Column<string>(type: "text", nullable: true),
                    legacy_html = table.Column<string>(type: "text", nullable: true),
                    is_seeded = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "display_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    display_id = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_display_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_display_events_displays_display_id",
                        column: x => x.display_id,
                        principalTable: "displays",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_display_events_display_created",
                table: "display_events",
                columns: new[] { "display_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_displays_identifier",
                table: "displays",
                column: "identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_templates_name",
                table: "templates",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "display_events");

            migrationBuilder.DropTable(
                name: "templates");

            migrationBuilder.DropTable(
                name: "displays");
        }
    }
}
