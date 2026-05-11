using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackRequestIdAndCleanFeedbackIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_match_feedback_step_match",
                table: "match_feedback");

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "match_feedback",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ux_match_feedback_request_step1",
                table: "match_feedback",
                columns: new[] { "RequestId", "Step" },
                unique: true,
                filter: "\"Step\" = 'DidYouFind'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_match_feedback_request_step1",
                table: "match_feedback");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "match_feedback");

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_step_match",
                table: "match_feedback",
                columns: new[] { "Step", "MatchId" });
        }
    }
}
