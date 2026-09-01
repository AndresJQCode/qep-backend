using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class CustomerExportEmailTemplateTests
{
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    // Una URL prefirmada real: media docena de parametros separados por `&`, que es justo lo que
    // rompe si el cuerpo HTML no los escapa.
    private const string SignedUrl =
        "https://r2.example/exports/f.xlsx?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
        "&X-Amz-Credential=abc%2F20260901%2Fauto%2Fs3%2Faws4_request" +
        "&X-Amz-Date=20260901T113000Z&X-Amz-Expires=86400&X-Amz-Signature=deadbeef";

    // El enlace y el vencimiento tienen que estar en los dos cuerpos: un cliente de correo que
    // solo renderiza texto plano dejaria al destinatario sin forma de descargar el archivo.
    [Fact]
    public void RenderPutsTheLinkAndTheExpiryInBothBodies()
    {
        var message = CustomerExportEmailTemplate.Render(
            "compras@verde.co",
            SignedUrl,
            "clientes-20260901-113000.xlsx",
            customerCount: 42,
            ExpiresAt);

        Assert.Equal("compras@verde.co", message.ToAddress);
        Assert.NotEmpty(message.Subject);

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("clientes-20260901-113000.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("42 clientes", body, StringComparison.Ordinal);
            Assert.Contains("01/09/2026 14:30 UTC", body, StringComparison.Ordinal);
        }

        // En texto plano la URL va cruda: escaparla ahi la romperia.
        Assert.Contains(SignedUrl, message.TextBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// El `&` sin escapar dentro de un `href` es lo que rompio el enlace en produccion: R2
    /// contesta `400 InvalidArgument / Authorization` cuando la query string le llega partida o
    /// con `&amp;` literal, y las dos cosas pasan cuando el HTML no declara la entidad.
    /// </summary>
    [Fact]
    public void RenderEscapesTheLinkInsideTheHtmlHref()
    {
        var message = CustomerExportEmailTemplate.Render(
            "compras@verde.co", SignedUrl, "clientes.xlsx", 1, ExpiresAt);

        Assert.Contains("&amp;X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("&X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
    }

    // Un solo cliente no se anuncia como "1 clientes".
    [Fact]
    public void RenderUsesTheSingularForOneCustomer()
    {
        var message = CustomerExportEmailTemplate.Render(
            "compras@verde.co", "https://r2.example/x", "clientes.xlsx", 1, ExpiresAt);

        Assert.Contains("(1 cliente)", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("1 clientes", message.TextBody, StringComparison.Ordinal);
    }
}
