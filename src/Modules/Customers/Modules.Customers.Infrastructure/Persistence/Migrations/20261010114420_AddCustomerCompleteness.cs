using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerCompleteness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "identification_type",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "identification_number",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "cuc",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "country",
                schema: "customers",
                table: "customers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2);

            migrationBuilder.AlterColumn<Guid>(
                name: "classification_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "address",
                schema: "customers",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "completeness",
                schema: "customers",
                table: "customers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Las filas viejas son fichas completas. EF pone "", que CK_customers_completeness rechaza.
                defaultValue: "Complete");

            migrationBuilder.AddColumn<string>(
                name: "whatsapp_user_id",
                schema: "customers",
                table: "customers",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_incomplete",
                schema: "customers",
                table: "customers",
                column: "tenant_id",
                filter: "completeness = 'Incomplete'");

            migrationBuilder.CreateIndex(
                name: "IX_customers_tenant_whatsapp_user_id",
                schema: "customers",
                table: "customers",
                columns: new[] { "tenant_id", "whatsapp_user_id" },
                unique: true,
                filter: "whatsapp_user_id IS NOT NULL");

            // El DEFAULT sólo sirvió para llenar las filas viejas: el código siempre escribe la columna, y un
            // INSERT que la olvide tiene que fallar en vez de crear una ficha "completa" sin datos.
            migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN completeness DROP DEFAULT;");
            // AddCustomerCityAndClassification dejó un DEFAULT Guid.Empty en classification_id que el modelo
            // no conoce. Con la columna anulable, un INSERT que no la nombre caería en una clasificación
            // inexistente (23503) en vez de quedar en NULL como un incompleto.
            migrationBuilder.Sql("ALTER TABLE customers.customers ALTER COLUMN classification_id DROP DEFAULT;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers",
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND address IS NOT NULL AND country IS NOT NULL AND classification_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_completeness",
                schema: "customers",
                table: "customers",
                sql: "completeness IN ('Complete', 'Incomplete')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Spec 2026-10-10 §7.2: un incompleto no tiene CUC, documento, dirección, país ni clasificación,
            // así que los SET NOT NULL de abajo morirían con 23502. No hay otra forma de volver al
            // esquema anterior que sacarlos; nacen sin libreta, así que no arrastran direcciones.
            migrationBuilder.Sql("DELETE FROM customers.customers WHERE completeness = 'Incomplete';");

            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_incomplete",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_customers_tenant_whatsapp_user_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_completeness",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "completeness",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "whatsapp_user_id",
                schema: "customers",
                table: "customers");

            migrationBuilder.AlterColumn<string>(
                name: "identification_type",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "identification_number",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "cuc",
                schema: "customers",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "country",
                schema: "customers",
                table: "customers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "classification_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "address",
                schema: "customers",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
