using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.ruutu.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "current_actions",
                table: "displays",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "forward_outcome",
                table: "display_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "current_actions",
                table: "displays");

            migrationBuilder.DropColumn(
                name: "forward_outcome",
                table: "display_events");
        }
    }
}
