using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAmbiguousIntentDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ambiguous_intent_drafts",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OriginalText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ambiguous_intent_drafts", x => x.Phone);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ambiguous_intent_drafts_created_at",
                table: "ambiguous_intent_drafts",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ambiguous_intent_drafts");
        }
    }
}
