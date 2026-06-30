using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.tietue.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "unique_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    type = table.Column<string>(type: "text", nullable: false),
                    field = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unique_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_unique_keys_entities_entity_id",
                        column: x => x.entity_id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_unique_keys_entity_id",
                table: "unique_keys",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_unique_keys_type_field_value",
                table: "unique_keys",
                columns: new[] { "type", "field", "value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unique_keys");
        }
    }
}
