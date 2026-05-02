using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_availabilities",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Services = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    Location = table.Column<Point>(type: "geography (Point, 4326)", nullable: false),
                    FormattedAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ShareContact = table.Column<bool>(type: "boolean", nullable: false),
                    LastActiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_availabilities", x => x.Phone);
                });

            migrationBuilder.CreateTable(
                name: "provider_registration_drafts",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DraftServices = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    DraftLatitude = table.Column<double>(type: "double precision", nullable: true),
                    DraftLongitude = table.Column<double>(type: "double precision", nullable: true),
                    DraftFormattedAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DraftShareContact = table.Column<bool>(type: "boolean", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_registration_drafts", x => x.Phone);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_availabilities_expires_at",
                table: "provider_availabilities",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "ix_provider_availabilities_location",
                table: "provider_availabilities",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_availabilities");

            migrationBuilder.DropTable(
                name: "provider_registration_drafts");
        }
    }
}
