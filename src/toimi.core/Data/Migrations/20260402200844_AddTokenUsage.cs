#pragma warning disable
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "completion_tokens",
                table: "conversation_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "prompt_tokens",
                table: "conversation_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_tokens",
                table: "conversation_messages",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "completion_tokens",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "prompt_tokens",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "total_tokens",
                table: "conversation_messages");
        }
    }
}
