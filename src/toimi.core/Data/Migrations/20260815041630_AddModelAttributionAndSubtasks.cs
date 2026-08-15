using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelAttributionAndSubtasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "conversations",
                type: "text",
                nullable: false,
                defaultValue: "chat");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_conversation_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "conversation_messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_parent_conversation_id",
                table: "conversations",
                column: "parent_conversation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_parent_conversation_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "parent_conversation_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "model",
                table: "conversation_messages");
        }
    }
}
