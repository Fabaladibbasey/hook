using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchUniqueRequestProviderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_matches_request_provider",
                table: "matches",
                columns: new[] { "RequestId", "ProviderPhone" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_matches_request_provider",
                table: "matches");
        }
    }
}
