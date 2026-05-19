using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceHierarchyAndMatchKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParentSlug",
                table: "services",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "matches",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValueSql: "'Exact'");

            migrationBuilder.CreateIndex(
                name: "ix_services_parent_slug",
                table: "services",
                column: "ParentSlug");

            migrationBuilder.AddForeignKey(
                name: "FK_services_services_ParentSlug",
                table: "services",
                column: "ParentSlug",
                principalTable: "services",
                principalColumn: "Slug",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_services_services_ParentSlug",
                table: "services");

            migrationBuilder.DropIndex(
                name: "ix_services_parent_slug",
                table: "services");

            migrationBuilder.DropColumn(
                name: "ParentSlug",
                table: "services");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "matches");
        }
    }
}
