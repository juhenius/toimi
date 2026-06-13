using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace toimi.tools.muistutin.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "reminders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "reminders");
        }
    }
}
