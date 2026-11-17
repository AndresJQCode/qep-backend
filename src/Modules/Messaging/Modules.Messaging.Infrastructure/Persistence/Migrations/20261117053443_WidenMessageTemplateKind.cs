using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WidenMessageTemplateKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "kind",
                schema: "messaging",
                table: "message_templates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Volver a 16 no cabe con una fila 'SupportFormalReply' (18): se descartan, como en el Down de
            // AddSupportFormalAndSurveyTemplateKinds.
            migrationBuilder.Sql("DELETE FROM messaging.message_templates WHERE length(kind) > 16;");

            migrationBuilder.AlterColumn<string>(
                name: "kind",
                schema: "messaging",
                table: "message_templates",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
