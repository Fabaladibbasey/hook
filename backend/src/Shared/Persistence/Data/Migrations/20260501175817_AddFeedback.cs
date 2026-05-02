using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "match_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Step = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Answer = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PromptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RepliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_stats",
                columns: table => new
                {
                    ProviderPhone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CompletedCount = table.Column<int>(type: "integer", nullable: false),
                    SuccessCount = table.Column<int>(type: "integer", nullable: false),
                    SuccessRate = table.Column<double>(type: "double precision", nullable: false),
                    LastUpdated = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_stats", x => x.ProviderPhone);
                });

            migrationBuilder.CreateIndex(
                name: "ix_match_feedback_match",
                table: "match_feedback",
                column: "MatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_feedback");

            migrationBuilder.DropTable(
                name: "provider_stats");
        }
    }
}
