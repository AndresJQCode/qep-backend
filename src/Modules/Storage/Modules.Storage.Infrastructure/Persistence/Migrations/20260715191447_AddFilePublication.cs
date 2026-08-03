using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Storage.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFilePublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "public_storage_key",
                schema: "storage",
                table: "file_resources",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "storage",
                table: "file_resources",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "public_storage_key",
                schema: "storage",
                table: "file_resources");

            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "storage",
                table: "file_resources");
        }
    }
}
