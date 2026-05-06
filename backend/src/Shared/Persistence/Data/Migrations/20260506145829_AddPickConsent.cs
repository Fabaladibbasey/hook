using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPickConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SharePhoneNumber",
                table: "service_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PickedAt",
                table: "matches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_matches_request_picked_at",
                table: "matches",
                columns: new[] { "RequestId", "PickedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_matches_request_picked_at",
                table: "matches");

            migrationBuilder.DropColumn(
                name: "SharePhoneNumber",
                table: "service_requests");

            migrationBuilder.DropColumn(
                name: "PickedAt",
                table: "matches");
        }
    }
}
