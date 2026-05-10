using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackEtaAndStepIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EtaUtc",
                table: "match_feedback",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_step_match",
                table: "match_feedback",
                columns: new[] { "Step", "MatchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_match_feedback_step_match",
                table: "match_feedback");

            migrationBuilder.DropColumn(
                name: "EtaUtc",
                table: "match_feedback");
        }
    }
}
