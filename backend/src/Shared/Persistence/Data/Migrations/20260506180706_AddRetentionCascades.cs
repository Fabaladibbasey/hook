using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionCascades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_chat_access_logs_chat_participants_ParticipantId",
                table: "chat_access_logs",
                column: "ParticipantId",
                principalTable: "chat_participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_chat_device_keys_chat_participants_ParticipantId",
                table: "chat_device_keys",
                column: "ParticipantId",
                principalTable: "chat_participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_chat_sessions_ChatId",
                table: "chat_messages",
                column: "ChatId",
                principalTable: "chat_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_chat_participants_chat_sessions_ChatId",
                table: "chat_participants",
                column: "ChatId",
                principalTable: "chat_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_matches_service_requests_RequestId",
                table: "matches",
                column: "RequestId",
                principalTable: "service_requests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_access_logs_chat_participants_ParticipantId",
                table: "chat_access_logs");

            migrationBuilder.DropForeignKey(
                name: "FK_chat_device_keys_chat_participants_ParticipantId",
                table: "chat_device_keys");

            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_chat_sessions_ChatId",
                table: "chat_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_chat_participants_chat_sessions_ChatId",
                table: "chat_participants");

            migrationBuilder.DropForeignKey(
                name: "FK_matches_service_requests_RequestId",
                table: "matches");
        }
    }
}
