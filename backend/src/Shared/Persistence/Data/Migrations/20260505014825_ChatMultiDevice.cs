using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatMultiDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Multi-device E2E redesign — old single-key ciphertexts cannot be re-keyed; drop rows.
            migrationBuilder.Sql("DELETE FROM chat_messages;");

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

            migrationBuilder.AddColumn<Guid>(
                name: "SenderDeviceId",
                table: "chat_messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "chat_device_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", maxLength: 200, nullable: false),
                    LastInboundSequence = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_device_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chat_message_recipients",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", maxLength: 5000, nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", maxLength: 12, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_message_recipients", x => new { x.MessageId, x.RecipientDeviceId });
                    table.ForeignKey(
                        name: "FK_chat_message_recipients_chat_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "chat_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_device_keys_chat",
                table: "chat_device_keys",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "ux_chat_device_keys_participant_device",
                table: "chat_device_keys",
                columns: new[] { "ParticipantId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chat_message_recipients_device",
                table: "chat_message_recipients",
                column: "RecipientDeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_device_keys");

            migrationBuilder.DropTable(
                name: "chat_message_recipients");

            migrationBuilder.DropColumn(
                name: "SenderDeviceId",
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
        }
    }
}
