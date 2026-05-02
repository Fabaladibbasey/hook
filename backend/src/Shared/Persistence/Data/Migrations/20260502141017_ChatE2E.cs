using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatE2E : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Text",
                table: "chat_messages");

            migrationBuilder.AddColumn<long>(
                name: "LastInboundSequence",
                table: "chat_participants",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "PublicKey",
                table: "chat_participants",
                type: "bytea",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Ciphertext",
                table: "chat_messages",
                type: "bytea",
                maxLength: 5000,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "Nonce",
                table: "chat_messages",
                type: "bytea",
                maxLength: 12,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "chat_messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastInboundSequence",
                table: "chat_participants");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "chat_participants");

            migrationBuilder.DropColumn(
                name: "Ciphertext",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "Nonce",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "chat_messages");

            migrationBuilder.AddColumn<string>(
                name: "Text",
                table: "chat_messages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }
    }
}
