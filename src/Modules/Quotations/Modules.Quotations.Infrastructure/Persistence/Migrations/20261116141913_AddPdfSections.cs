using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPdfSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pdf_sections",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    in_quotation = table.Column<bool>(type: "boolean", nullable: false),
                    in_order = table.Column<bool>(type: "boolean", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    content_html = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pdf_sections", x => new { x.tenant_id, x.section });
                    table.CheckConstraint("CK_pdf_sections_section", "section IN ('PaymentNotice', 'Terms', 'DeliveryProcess')");
                });

            // Origen Botánico conserva los dos textos que quotation.typ le imprimía a todos (spec 2026-11-16 §5.5), ya como HTML
            // saneado: es un punto fijo del saneador (PdfContentMigrationTests). Los demás tenants quedan sin filas, o sea apagados.
            // Se lee de tenancy.tenants, que migra antes que Quotations al arrancar (Program.cs); la guarda deja correr la migración
            // en una base que sólo tiene Quotations, como AddQuotationSettings.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('tenancy.tenants') IS NOT NULL THEN
                        INSERT INTO quotations.pdf_sections
                            (tenant_id, section, enabled, in_quotation, in_order, title, content_html, updated_at)
                        SELECT t.id, s.section, true, true, true, s.title, s.content_html, now()
                        FROM tenancy.tenants t
                        CROSS JOIN (VALUES
                            ('PaymentNotice',
                             'Ten en cuenta:',
                             '<p>no aceptamos pagos por corresponsal bancario,<br>ni depósitos en entidades bancarias, ni cajeros.<br>Solo debes pagar el valor de tu pedido, el valor a pagar<br>debe de ser libre de toda comisión bancaria.</p>'),
                            ('DeliveryProcess',
                             'Proceso de Alistamiento y Recepción de Pedidos.',
                             '<p><strong>Alistamiento y Entrega:</strong><br>El proveedor preparará los pedidos bajo filmación y los entregará a la empresa transportadora dentro de los 8 días hábiles siguientes a la confirmación del pedido, siempre que estos hayan sido solicitados y pagados por el mayorista.</p><p><strong>Recepción de la Mercancía:</strong><br>Al recibir los productos de la transportadora, el mayorista deberá revisar el estado del paquete. No recibir en caso de detectar:</p><ul><li>Cinta contramarcada deteriorada</li><li>Cajas dañadas y/o abiertas</li><li>Cintas, zunchos o cajas diferentes a las del proveedor</li></ul><p>Si decide recibir el paquete a pesar de alguna de estas irregularidades, lo hará bajo su responsabilidad. En este caso, deberá:</p><ul><li>Dejar constancia de la novedad en la guía de recepción de la transportadora.</li><li>Informar de inmediato al proveedor.</li><li>Enviar fotografías como evidencia.</li></ul><p><strong>Verificación del Contenido:</strong><br>Una vez recibido el pedido, el mayorista deberá grabar un video al abrir las cajas o paquetes, evidenciando el estado y la cantidad de los productos.</p><p><strong>Reporte de Novedades:</strong><br>Si detecta daños, faltantes o deterioro en los productos, deberá informar al proveedor en un plazo máximo de 1 día hábil después de recibirlos. El reporte debe incluir:</p><ul><li>Video de la apertura del paquete.</li><li>Fotografías de las cajas.</li><li>Imágenes de los 3 stickers identificativos del proveedor.</li></ul>')
                        ) AS s (section, title, content_html)
                        WHERE t.slug = 'origen-botanico'
                        ON CONFLICT DO NOTHING;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pdf_sections",
                schema: "quotations");
        }
    }
}
