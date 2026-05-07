using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSweepIndexesAndCascades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_contacts_last_inbound_at",
                table: "whatsapp_contacts",
                column: "LastInboundAt");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_created_at",
                table: "service_requests",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_provider_registration_drafts_updated_at",
                table: "provider_registration_drafts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_prompted_at",
                table: "match_feedback",
                column: "PromptedAt");

            migrationBuilder.CreateIndex(
                name: "ix_geocode_cache_fetched_at",
                table: "geocode_cache",
                column: "FetchedAt");

            migrationBuilder.CreateIndex(
                name: "ix_client_request_drafts_updated_at",
                table: "client_request_drafts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_chat_access_logs_ChatId",
                table: "chat_access_logs",
                column: "ChatId");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_access_logs_chat_sessions_ChatId",
                table: "chat_access_logs",
                column: "ChatId",
                principalTable: "chat_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_chat_device_keys_chat_sessions_ChatId",
                table: "chat_device_keys",
                column: "ChatId",
                principalTable: "chat_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_match_feedback_matches_MatchId",
                table: "match_feedback",
                column: "MatchId",
                principalTable: "matches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_access_logs_chat_sessions_ChatId",
                table: "chat_access_logs");

            migrationBuilder.DropForeignKey(
                name: "FK_chat_device_keys_chat_sessions_ChatId",
                table: "chat_device_keys");

            migrationBuilder.DropForeignKey(
                name: "FK_match_feedback_matches_MatchId",
                table: "match_feedback");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_contacts_last_inbound_at",
                table: "whatsapp_contacts");

            migrationBuilder.DropIndex(
                name: "ix_service_requests_created_at",
                table: "service_requests");

            migrationBuilder.DropIndex(
                name: "ix_provider_registration_drafts_updated_at",
                table: "provider_registration_drafts");

            migrationBuilder.DropIndex(
                name: "ix_match_feedback_prompted_at",
                table: "match_feedback");

            migrationBuilder.DropIndex(
                name: "ix_geocode_cache_fetched_at",
                table: "geocode_cache");

            migrationBuilder.DropIndex(
                name: "ix_client_request_drafts_updated_at",
                table: "client_request_drafts");

            migrationBuilder.DropIndex(
                name: "IX_chat_access_logs_ChatId",
                table: "chat_access_logs");
        }
    }
}
