using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Suppliers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierLinkedTenant : Migration
    {
        /// <summary>Spec 2026-11-17 (mayorista), D-8: el mayorista que el proveedor representa en QEP. Aditiva: una columna
        /// nullable (los proveedores existentes quedan externos) y un índice único parcial, uno por mayorista dentro del
        /// tenant.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "linked_tenant_id",
                schema: "suppliers",
                table: "suppliers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_tenant_linked_tenant",
                schema: "suppliers",
                table: "suppliers",
                columns: new[] { "tenant_id", "linked_tenant_id" },
                unique: true,
                filter: "linked_tenant_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_suppliers_tenant_linked_tenant",
                schema: "suppliers",
                table: "suppliers");

            migrationBuilder.DropColumn(
                name: "linked_tenant_id",
                schema: "suppliers",
                table: "suppliers");
        }
    }
}
