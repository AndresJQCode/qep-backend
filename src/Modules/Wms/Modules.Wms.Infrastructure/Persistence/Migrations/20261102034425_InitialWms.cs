using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wms");

            migrationBuilder.CreateTable(
                name: "identifiers",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    symbology = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identifiers", x => x.id);
                    table.CheckConstraint("CK_identifiers_kind", "kind IN ('Variant','Presentation','Lot','LogisticUnit')");
                    table.CheckConstraint("CK_identifiers_symbology", "symbology IN ('Ean13','Ean8','Upc','Code128','Qr','Gtin','Sscc','Internal')");
                });

            migrationBuilder.CreateTable(
                name: "member_scopes",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_scopes", x => x.id);
                    table.CheckConstraint("CK_member_scopes_target", "owner_id IS NOT NULL OR warehouse_id IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "owners",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    identification = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    contact_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owners", x => x.id);
                    table.CheckConstraint("CK_owners_kind", "kind IN ('Self','Client')");
                });

            migrationBuilder.CreateTable(
                name: "units_of_measure",
                schema: "wms",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    precision = table.Column<int>(type: "integer", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_units_of_measure", x => new { x.tenant_id, x.code });
                    table.CheckConstraint("CK_units_of_measure_precision", "precision BETWEEN 0 AND 6");
                });

            migrationBuilder.CreateTable(
                name: "variant_profiles",
                schema: "wms",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_uom = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    base_uom_precision = table.Column<int>(type: "integer", nullable: false),
                    tracking = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    tracks_expiry = table.Column<bool>(type: "boolean", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    volume_m3 = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    length_cm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    width_cm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    height_cm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    storage_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    display_uom = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_variant_profiles", x => new { x.tenant_id, x.variant_id });
                    table.CheckConstraint("CK_variant_profiles_precision", "base_uom_precision BETWEEN 0 AND 6");
                    table.CheckConstraint("CK_variant_profiles_tracking", "tracking IN ('None','Lot','Serial')");
                });

            migrationBuilder.CreateTable(
                name: "warehouses",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "uom_conversions",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    factor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uom_conversions", x => x.id);
                    table.CheckConstraint("CK_uom_conversions_factor", "factor > 0");
                    table.ForeignKey(
                        name: "FK_uom_conversions_profile",
                        columns: x => new { x.tenant_id, x.variant_id },
                        principalSchema: "wms",
                        principalTable: "variant_profiles",
                        principalColumns: new[] { "tenant_id", "variant_id" },
                        // A mano: el modelo declara ClientCascade (EF borra del lado del cliente la conversión que el perfil deja
                        // caer) y el generador no emite nada para eso, lo que en la base sería NO ACTION. El esquema exige RESTRICT
                        // (spec §11.2): borrar un perfil con conversiones vivas no puede arrastrarlas en silencio.
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "wms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_virtual = table.Column<bool>(type: "boolean", nullable: false),
                    allows_storage = table.Column<bool>(type: "boolean", nullable: false),
                    allows_picking = table.Column<bool>(type: "boolean", nullable: false),
                    putaway_priority = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    storage_classes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    max_units = table.Column<int>(type: "integer", nullable: true),
                    max_volume_m3 = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    max_weight_kg = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_locations", x => x.id);
                    table.CheckConstraint("CK_locations_kind", "kind IN ('Zone','Aisle','Rack','Level','Bin','Dock','Virtual')");
                    table.CheckConstraint("CK_locations_role", "role IN ('Receiving','Storage','Picking','Shipping','Quarantine','Returns','Transit','Adjustment','Supplier','Customer')");
                    table.CheckConstraint("CK_locations_status", "status IN ('Active','Blocked','Counting')");
                    table.CheckConstraint("CK_locations_virtual_storage", "NOT is_virtual OR NOT allows_storage");
                    table.ForeignKey(
                        name: "FK_locations_parent",
                        column: x => x.parent_id,
                        principalSchema: "wms",
                        principalTable: "locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_locations_warehouse",
                        column: x => x.warehouse_id,
                        principalSchema: "wms",
                        principalTable: "warehouses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_identifiers_tenant_target",
                schema: "wms",
                table: "identifiers",
                columns: new[] { "tenant_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "IX_identifiers_tenant_value",
                schema: "wms",
                table: "identifiers",
                columns: new[] { "tenant_id", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_parent_id",
                schema: "wms",
                table: "locations",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_locations_path",
                schema: "wms",
                table: "locations",
                column: "path")
                .Annotation("Npgsql:IndexMethod", "btree")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_locations_warehouse_code",
                schema: "wms",
                table: "locations",
                columns: new[] { "warehouse_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_locations_warehouse_role",
                schema: "wms",
                table: "locations",
                columns: new[] { "warehouse_id", "role" });

            migrationBuilder.CreateIndex(
                name: "IX_locations_warehouse_virtual_role",
                schema: "wms",
                table: "locations",
                columns: new[] { "warehouse_id", "role" },
                unique: true,
                filter: "is_virtual");

            migrationBuilder.CreateIndex(
                name: "IX_member_scopes_tenant_member",
                schema: "wms",
                table: "member_scopes",
                columns: new[] { "tenant_id", "member_id" });

            migrationBuilder.CreateIndex(
                name: "IX_owners_tenant",
                schema: "wms",
                table: "owners",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_owners_tenant_self",
                schema: "wms",
                table: "owners",
                column: "tenant_id",
                unique: true,
                filter: "kind = 'Self'");

            migrationBuilder.CreateIndex(
                name: "IX_uom_conversions_variant_code",
                schema: "wms",
                table: "uom_conversions",
                columns: new[] { "tenant_id", "variant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_warehouses_tenant_code",
                schema: "wms",
                table: "warehouses",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identifiers",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "locations",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "member_scopes",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "owners",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "units_of_measure",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "uom_conversions",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "warehouses",
                schema: "wms");

            migrationBuilder.DropTable(
                name: "variant_profiles",
                schema: "wms");
        }
    }
}
