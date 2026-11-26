using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-21 siigo-motivo-pendiente: una factura que sigue <c>Pending</c> porque Siigo no contestó guarda por qué, para que el
/// pedido se lo diga al tenant. El motivo viaja como código <c>siigo.invoice.*</c>, nunca como texto de Siigo.
/// </summary>
public sealed class SiigoPendingReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static SiigoInvoice NewPending() =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), SiigoInvoiceKind.Order, """{"a":1}""", 7, Guid.CreateVersion7(), Now);

    [Theory]
    [InlineData("timeout", "siigo.invoice.timeout")]
    [InlineData("network", "siigo.invoice.network")]
    [InlineData("http_429", "siigo.invoice.rate_limited")]
    [InlineData("http_502", "siigo.invoice.unavailable")]
    [InlineData("http_503", "siigo.invoice.unavailable")]
    [InlineData("partner_id_missing", "siigo.invoice.unavailable")]
    [InlineData("auth_without_token", "siigo.invoice.unavailable")]
    [InlineData("something_new", "siigo.invoice.unavailable")]
    public void ATransientReasonBecomesItsInvoiceCode(string reason, string expected) =>
        Assert.Equal(expected, SiigoInvoiceSupport.TransientCodeOf(reason));

    [Fact]
    public void AMarkedTransientInvoiceStaysPendingWithItsReason()
    {
        var invoice = NewPending();
        invoice.BeginSend(Now);

        invoice.MarkTransient(SiigoErrorCodes.InvoiceTimeout, Now.AddSeconds(30));

        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal("siigo.invoice.timeout", invoice.ErrorCode);
        Assert.Null(invoice.ErrorDetail);
        Assert.False(invoice.IsBeingSent(Now.AddSeconds(31)));
    }

    [Fact]
    public void AMarkWithoutACodeKeepsTheOneThatWasThere()
    {
        var invoice = NewPending();
        invoice.MarkTransient(SiigoErrorCodes.InvoiceNetwork, Now);

        invoice.MarkTransient(null, Now.AddMinutes(1));

        Assert.Equal("siigo.invoice.network", invoice.ErrorCode);
    }

    [Fact]
    public void ARetryThatSucceedsClearsTheReason()
    {
        var invoice = NewPending();
        invoice.MarkTransient(SiigoErrorCodes.InvoiceUnavailable, Now);

        invoice.MarkCreated("id-1", "FV-1-1", 100m, null, Now.AddMinutes(1));

        Assert.Null(invoice.ErrorCode);
    }
}
