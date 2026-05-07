using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingFeedbackUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_match_feedback_pending",
                table: "match_feedback",
                columns: new[] { "MatchId", "Step" },
                unique: true,
                filter: "\"Answer\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_match_feedback_pending",
                table: "match_feedback");
        }
    }
}
