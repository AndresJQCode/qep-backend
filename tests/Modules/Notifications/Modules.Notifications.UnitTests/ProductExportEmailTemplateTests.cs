using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>El correo de exportación de productos: mismo contrato que el de clientes, y el
/// vencimiento del enlace en la hora del tenant (spec 2026-09-17, punto 8b).</summary>
public sealed class ProductExportEmailTemplateTests
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    [Fact]
    public void RenderShowsTheExpiryInTheTenantsLocalTimeWithoutAZoneLabel()
    {
        var message = ProductExportEmailTemplate.Render(
            "compras@verde.co",
            "https://r2.example/exports/productos.xlsx",
            "productos-2026-12-31-2300.xlsx",
            productCount: 3,
            new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero),
            Bogota);

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("productos-2026-12-31-2300.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("3 productos", body, StringComparison.Ordinal);
            Assert.Contains("31/12/2026 23:00", body, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", body, StringComparison.Ordinal);
        }
    }
}
