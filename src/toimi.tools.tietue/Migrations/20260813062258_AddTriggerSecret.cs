using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.tietue.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "secret",
                table: "triggers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "secret",
                table: "triggers");
        }
    }
}
