using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_request_drafts",
                columns: table => new
                {
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Step = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DraftServiceSlug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DraftLatitude = table.Column<double>(type: "double precision", nullable: true),
                    DraftLongitude = table.Column<double>(type: "double precision", nullable: true),
                    DraftFormattedAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DraftDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_request_drafts", x => x.Phone);
                });

            migrationBuilder.CreateTable(
                name: "service_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientPhone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ServiceSlug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Location = table.Column<Point>(type: "geography (Point, 4326)", nullable: false),
                    FormattedAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShownProviderPhones = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    CurrentRadiusKm = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_client_phone",
                table: "service_requests",
                column: "ClientPhone");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_location",
                table: "service_requests",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_service_requests_status",
                table: "service_requests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_request_drafts");

            migrationBuilder.DropTable(
                name: "service_requests");
        }
    }
}
