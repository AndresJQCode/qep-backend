using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "processed_at",
                schema: "identity",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "identity",
                table: "inbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_until",
                schema: "identity",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las filas reclamadas y sin terminar se borran antes de volver processed_at
            // obligatorio: si no, el rollback las sellaría con 0001-01-01 y el worker de antes
            // las daría por procesadas. Borradas, el mensaje vuelve a la cola.
            migrationBuilder.Sql("DELETE FROM identity.inbox_messages WHERE processed_at IS NULL;");

            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "identity",
                table: "inbox_messages");

            migrationBuilder.DropColumn(
                name: "claimed_until",
                schema: "identity",
                table: "inbox_messages");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "processed_at",
                schema: "identity",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
