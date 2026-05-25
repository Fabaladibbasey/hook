using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatAggregateConcurrencyVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "chat_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "chat_participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "chat_sessions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "chat_participants");
        }
    }
}
