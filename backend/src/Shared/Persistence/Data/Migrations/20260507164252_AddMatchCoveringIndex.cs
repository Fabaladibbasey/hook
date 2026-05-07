using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_matches_request_score_distance_created_id",
                table: "matches",
                columns: new[] { "RequestId", "Score", "DistanceKm", "CreatedAt", "Id" },
                descending: new[] { false, true, false, false, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_matches_request_score_distance_created_id",
                table: "matches");
        }
    }
}
