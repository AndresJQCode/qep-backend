using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class CustomerExportEmailTemplateTests
{
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    // El enlace y el vencimiento tienen que estar en los dos cuerpos: un cliente de correo que
    // solo renderiza texto plano dejaria al destinatario sin forma de descargar el archivo.
    [Fact]
    public void RenderPutsTheLinkAndTheExpiryInBothBodies()
    {
        var message = CustomerExportEmailTemplate.Render(
            "compras@verde.co",
            "https://r2.example/exports/signed?sig=abc",
            "clientes-20260901-113000.xlsx",
            customerCount: 42,
            ExpiresAt);

        Assert.Equal("compras@verde.co", message.ToAddress);
        Assert.NotEmpty(message.Subject);

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains(
                "https://r2.example/exports/signed?sig=abc", body, StringComparison.Ordinal);
            Assert.Contains("clientes-20260901-113000.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("42 clientes", body, StringComparison.Ordinal);
            Assert.Contains("01/09/2026 14:30 UTC", body, StringComparison.Ordinal);
        }
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
