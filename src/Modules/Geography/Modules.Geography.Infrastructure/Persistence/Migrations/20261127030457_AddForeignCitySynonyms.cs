using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignCitySynonyms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "foreign_city_synonyms",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    billing_city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_foreign_city_synonyms", x => x.id);
                    table.ForeignKey(
                        name: "FK_foreign_city_synonyms_billing_cities_billing_city_id",
                        column: x => x.billing_city_id,
                        principalSchema: "geography",
                        principalTable: "billing_cities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unresolved_foreign_cities",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    normalized_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city_text = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    synonym_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unresolved_foreign_cities", x => x.id);
                    table.CheckConstraint("CK_unresolved_foreign_cities_status", "status IN ('Pending','Resolved','Discarded')");
                    table.ForeignKey(
                        name: "FK_unresolved_foreign_cities_foreign_city_synonyms_synonym_id",
                        column: x => x.synonym_id,
                        principalSchema: "geography",
                        principalTable: "foreign_city_synonyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unresolved_foreign_city_occurrences",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    unresolved_city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unresolved_foreign_city_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "FK_unresolved_foreign_city_occurrences_unresolved_foreign_citi~",
                        column: x => x.unresolved_city_id,
                        principalSchema: "geography",
                        principalTable: "unresolved_foreign_cities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_foreign_city_synonyms_billing_city",
                schema: "geography",
                table: "foreign_city_synonyms",
                column: "billing_city_id");

            migrationBuilder.CreateIndex(
                name: "IX_foreign_city_synonyms_country_text_active",
                schema: "geography",
                table: "foreign_city_synonyms",
                columns: new[] { "country_code", "normalized_text" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_foreign_cities_country_text_open",
                schema: "geography",
                table: "unresolved_foreign_cities",
                columns: new[] { "country_code", "normalized_text" },
                unique: true,
                filter: "status <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_foreign_cities_status_last_seen",
                schema: "geography",
                table: "unresolved_foreign_cities",
                columns: new[] { "status", "last_seen_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_foreign_cities_synonym_id",
                schema: "geography",
                table: "unresolved_foreign_cities",
                column: "synonym_id");

            migrationBuilder.CreateIndex(
                name: "IX_unresolved_foreign_city_occurrences_city_order",
                schema: "geography",
                table: "unresolved_foreign_city_occurrences",
                columns: new[] { "unresolved_city_id", "order_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unresolved_foreign_city_occurrences",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "unresolved_foreign_cities",
                schema: "geography");

            migrationBuilder.DropTable(
                name: "foreign_city_synonyms",
                schema: "geography");
        }
    }
}
