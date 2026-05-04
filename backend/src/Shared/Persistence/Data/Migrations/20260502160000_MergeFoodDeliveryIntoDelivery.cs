using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hook.Shared.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeFoodDeliveryIntoDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ensure canonical 'delivery' service row exists before re-pointing FKs.
            migrationBuilder.Sql("""
                INSERT INTO services ("Slug", "CreatedAt", "RawExamples")
                SELECT 'delivery', NOW(), '[]'::jsonb
                WHERE NOT EXISTS (SELECT 1 FROM services WHERE "Slug" = 'delivery');
                """);

            // Scalar slug columns.
            migrationBuilder.Sql("""
                UPDATE service_requests       SET "ServiceSlug"      = 'delivery' WHERE "ServiceSlug"      = 'food-delivery';
                UPDATE matches                SET "ServiceSlug"      = 'delivery' WHERE "ServiceSlug"      = 'food-delivery';
                UPDATE client_request_drafts  SET "DraftServiceSlug" = 'delivery' WHERE "DraftServiceSlug" = 'food-delivery';
                """);

            // Jsonb array columns: replace 'food-delivery' element with 'delivery' and dedupe.
            migrationBuilder.Sql("""
                UPDATE provider_availabilities
                SET "Services" = COALESCE((
                    SELECT jsonb_agg(DISTINCT replaced)
                    FROM (
                        SELECT CASE WHEN val = 'food-delivery' THEN 'delivery' ELSE val END AS replaced
                        FROM jsonb_array_elements_text("Services") AS t(val)
                    ) sub
                ), '[]'::jsonb)
                WHERE "Services" ? 'food-delivery';
                """);

            migrationBuilder.Sql("""
                UPDATE provider_registration_drafts
                SET "DraftServices" = COALESCE((
                    SELECT jsonb_agg(DISTINCT replaced)
                    FROM (
                        SELECT CASE WHEN val = 'food-delivery' THEN 'delivery' ELSE val END AS replaced
                        FROM jsonb_array_elements_text("DraftServices") AS t(val)
                    ) sub
                ), '[]'::jsonb)
                WHERE "DraftServices" ? 'food-delivery';
                """);

            // Drop the legacy slug row last; nothing should reference it anymore.
            migrationBuilder.Sql("""
                DELETE FROM services WHERE "Slug" = 'food-delivery';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only merge is destructive of the food-delivery slug; not reversible.
        }
    }
}
