using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClosedRequestSweepIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_requests_created_at",
                table: "service_requests");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_closed_created_at",
                table: "service_requests",
                column: "CreatedAt",
                filter: "\"Status\" = 'Closed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_requests_closed_created_at",
                table: "service_requests");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_created_at",
                table: "service_requests",
                column: "CreatedAt");
        }
    }
}
