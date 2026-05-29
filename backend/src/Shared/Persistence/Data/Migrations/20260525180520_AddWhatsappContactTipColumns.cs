using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsappContactTipColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTipAt",
                table: "whatsapp_contacts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "'1970-01-01 00:00:00+00'");

            migrationBuilder.AddColumn<string>(
                name: "LastTipKey",
                table: "whatsapp_contacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTipAt",
                table: "whatsapp_contacts");

            migrationBuilder.DropColumn(
                name: "LastTipKey",
                table: "whatsapp_contacts");
        }
    }
}
