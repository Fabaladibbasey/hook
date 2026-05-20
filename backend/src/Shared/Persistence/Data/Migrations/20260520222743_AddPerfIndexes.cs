using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerfIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_requests_client_phone",
                table: "service_requests");

            migrationBuilder.DropIndex(
                name: "ix_service_requests_status",
                table: "service_requests");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_client_phone_status",
                table: "service_requests",
                columns: new[] { "ClientPhone", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_pending_prompted_at",
                table: "match_feedback",
                columns: new[] { "Answer", "PromptedAt" },
                descending: new[] { false, true },
                filter: "\"Answer\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_requests_client_phone_status",
                table: "service_requests");

            migrationBuilder.DropIndex(
                name: "ix_match_feedback_pending_prompted_at",
                table: "match_feedback");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_client_phone",
                table: "service_requests",
                column: "ClientPhone");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_status",
                table: "service_requests",
                column: "Status");
        }
    }
}
