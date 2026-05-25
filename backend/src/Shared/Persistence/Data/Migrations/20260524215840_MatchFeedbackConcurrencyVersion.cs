using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchFeedbackConcurrencyVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "match_feedback",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "match_feedback");
        }
    }
}
