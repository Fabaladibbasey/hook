using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJudgeParentDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "judge_parent_dedup",
                columns: table => new
                {
                    Slug = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    JudgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_judge_parent_dedup", x => x.Slug);
                });

            migrationBuilder.CreateIndex(
                name: "ix_judge_parent_dedup_judged_at",
                table: "judge_parent_dedup",
                column: "JudgedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "judge_parent_dedup");
        }
    }
}
