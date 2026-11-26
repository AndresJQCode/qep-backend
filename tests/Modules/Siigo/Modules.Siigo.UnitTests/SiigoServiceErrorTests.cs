using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Api;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Los errores de servicio de Siigo (<c>documents_service</c> y afines) llegan como un 400 con su código, pero son una caída de Siigo
/// y no un dato mal enviado: la pantalla tiene que decirle al tenant que es un problema de Siigo y no de la plataforma.
/// </summary>
public sealed class SiigoServiceErrorTests
{
    private const string DocumentsServiceBody =
        """{"Status":400,"Errors":[{"Code":"documents_service","Message":"Your request could not be completed with the data you submitted. Please verify","Params":[""],"Detail":"Check the API documentation"}]}""";

    [Theory]
    [InlineData("documents_service")]
    [InlineData("payment_types_service")]
    [InlineData("general_service")]
    [InlineData("service_unavailable")]
    [InlineData("request_timeout")]
    [InlineData("DOCUMENTS_SERVICE")]
    public void AServiceErrorIsASiigoOutage(string code) =>
        Assert.True(SiigoApiErrors.IsServiceUnavailable([new SiigoError(code, "x")]));

    [Theory]
    [InlineData("invalid_total_payments")]
    [InlineData("document_settings")]
    [InlineData("invalid_reference")]
    [InlineData("unhandled_error")]
    public void ADataErrorIsNotAnOutage(string code) =>
        Assert.False(SiigoApiErrors.IsServiceUnavailable([new SiigoError(code, "x")]));

    [Fact]
    public void AServiceErrorMixedWithADataErrorIsNotAnOutage() =>
        Assert.False(SiigoApiErrors.IsServiceUnavailable([new SiigoError("documents_service", "x"), new SiigoError("invalid_reference", "x")]));

    [Fact]
    public void NoErrorsIsNotAnOutage() => Assert.False(SiigoApiErrors.IsServiceUnavailable([]));

    [Fact]
    public void ARejectedInvoiceWithAServiceErrorGetsTheSiigoServiceCode() =>
        Assert.Equal(
            "siigo.invoice.siigo_service",
            SiigoInvoiceSupport.ErrorCodeOf([new SiigoError("documents_service", "Your request could not be completed")]));

    [Fact]
    public void TheRealSiigoBodyParsesAsAnOutage()
    {
        var errors = SiigoJson.Errors(DocumentsServiceBody, System.Net.HttpStatusCode.BadRequest);

        Assert.True(SiigoApiErrors.IsServiceUnavailable(errors));
        Assert.Equal("documents_service", SiigoInvoiceSupport.ErrorDetailOf(errors));
    }
}
