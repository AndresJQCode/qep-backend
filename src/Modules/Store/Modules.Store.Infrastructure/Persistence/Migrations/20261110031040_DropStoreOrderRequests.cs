using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropStoreOrderRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_request_lines",
                schema: "store");

            migrationBuilder.DropTable(
                name: "request_counters",
                schema: "store");

            migrationBuilder.DropTable(
                name: "order_requests",
                schema: "store");
        }

        // La bandeja se quitó (spec 2026-11-09, D6): no hay modelo al que volver ni datos que reponer.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("The store inbox was removed (spec 2026-11-09, D6).");
    }
}
