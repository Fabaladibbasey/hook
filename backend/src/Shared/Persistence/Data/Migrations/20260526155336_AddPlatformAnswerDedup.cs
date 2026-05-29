using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAnswerDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_answer_dedup",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QuestionHash = table.Column<long>(type: "bigint", nullable: false),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_answer_dedup", x => new { x.Phone, x.QuestionHash });
                });

            migrationBuilder.CreateIndex(
                name: "ix_platform_answer_dedup_answered_at",
                table: "platform_answer_dedup",
                column: "AnsweredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_answer_dedup");
        }
    }
}
