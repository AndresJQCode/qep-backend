using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El documento de quien recibe la factura cuando no es el cliente (pedido del owner, 2026-09-26:
/// "sin ese número no se puede facturar"). Viaja como <c>identificationNumber</c> al lado de
/// <c>name</c> en la parte, en el request y en la respuesta. Estas pruebas cuidan las dos puntas
/// del contrato y que el 422 traiga el campo en el mapa <c>errors</c>, que es lo único con lo que
/// el formulario sabe marcar el input.
/// </summary>
public sealed class QuotationBillingIdentificationContractTests
{
    private const string ErrorKey = "Parties.Billing.IdentificationNumber";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void APartyRequestBindsIdentificationNumberFromCamelCaseAndReachesTheDomain()
    {
        var request = JsonSerializer.Deserialize<QuotationPartiesRequest>(
            """{ "billing": { "name": "Sede administrativa", "identificationNumber": "1020304050" }, "shipping": null }""",
            Web);

        Assert.NotNull(request);
        Assert.Equal("1020304050", request.Billing?.IdentificationNumber);
        Assert.Equal("1020304050", request.ToDomain().Billing?.IdentificationNumber);
    }

    [Fact]
    public void TheDtoCarriesTheBillingPartysIdentificationNumber()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), Guid.CreateVersion7(), "QUO-2026-0001", Guid.CreateVersion7(),
            new MemberId(Guid.CreateVersion7()), new DateOnly(2026, 10, 30), null, null,
            new QuotationParties(
                new QuotationPartyDetails { Name = "Sede administrativa", IdentificationNumber = "1020304050" },
                Shipping: null),
            null, QuotationCurrency.Cop, false, false, new MemberId(Guid.CreateVersion7()), DateTimeOffset.UtcNow);

        var party = Assert.Single(quotation.ToDto().Parties);

        Assert.Equal("1020304050", party.IdentificationNumber);
    }

    [Fact]
    public void CreateRejectsABillingPartyWithANameButNoIdentificationUnderTheFieldKey()
    {
        var result = new CreateQuotationValidator().Validate(CreateCommand(
            new QuotationPartiesRequest(Party("Sede administrativa", "  "), null)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKey, error.PropertyName);
    }

    [Fact]
    public void SaveRejectsABillingPartyWithANameButNoIdentificationUnderTheFieldKey()
    {
        var result = new SaveQuotationValidator().Validate(SaveCommand(
            new QuotationPartiesRequest(Party("Sede administrativa", null), null)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKey, error.PropertyName);
    }

    // El cálculo previo corre mientras la persona escribe: acaba de poner el nombre y todavía no
    // el número. Exigirlo ahí sería un 422 en cada tecla; lo exigen el guardado y las transiciones.
    [Fact]
    public void PreviewDoesNotRequireTheIdentificationWhileTheUserIsTyping()
    {
        var result = new PreviewQuotationValidator().Validate(Preview(
            new QuotationPartiesRequest(Party("Sede administrativa", ""), null)));

        Assert.True(result.IsValid);
    }

    // El tope sí: un número demasiado largo no se arregla escribiendo más.
    [Fact]
    public void PreviewStillRejectsAnIdentificationLongerThanTheCustomersOne()
    {
        var result = new PreviewQuotationValidator().Validate(Preview(
            new QuotationPartiesRequest(
                Party("Sede administrativa", new string('9', QuotationPartyDetails.IdentificationNumberMaxLength + 1)),
                null)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKey, error.PropertyName);
    }

    // La entrega siempre llega con identificationNumber vacío desde el formulario: se ignora, no
    // se valida.
    [Fact]
    public void AShippingIdentificationIsNeverValidated()
    {
        var result = new SaveQuotationValidator().Validate(SaveCommand(
            new QuotationPartiesRequest(null, Party("Bodega Fontibon", new string('9', 40)))));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AnIdentificationLongerThanTheCustomersOneIsRejectedUnderTheFieldKey()
    {
        var result = new SaveQuotationValidator().Validate(SaveCommand(
            new QuotationPartiesRequest(
                Party("Sede administrativa", new string('9', QuotationPartyDetails.IdentificationNumberMaxLength + 1)),
                null)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorKey, error.PropertyName);
    }

    // Sin nombre propio la factura sigue saliendo al cliente: no hay a quién pedirle el número.
    [Fact]
    public void ABillingPartyWithoutANameDoesNotNeedAnIdentification()
    {
        var result = new SaveQuotationValidator().Validate(SaveCommand(
            new QuotationPartiesRequest(Party(null, null) with { Phone = "3105550134" }, null)));

        Assert.True(result.IsValid);
    }

    // Consumidor final con parte propia es otro error, con su propio código de dominio
    // (quotation.billing.final_consumer_conflict): el validador no lo tapa con un 422 de campo.
    [Fact]
    public void TheFinalConsumerConflictIsLeftToTheDomain()
    {
        var result = new SaveQuotationValidator().Validate(SaveCommand(
            new QuotationPartiesRequest(Party("Sede administrativa", null), null, BillsToFinalConsumer: true)));

        Assert.True(result.IsValid);
    }

    private static QuotationPartyRequest Party(string? name, string? identificationNumber) =>
        new(name, identificationNumber, null, null, null, null, null);

    private static CreateQuotationCommand CreateCommand(QuotationPartiesRequest parties) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, null, parties, null);

    private static PreviewQuotationQuery Preview(QuotationPartiesRequest parties) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, null, null, parties, null, [], null, false);

    private static SaveQuotationCommand SaveCommand(QuotationPartiesRequest parties) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, null, null, null, parties, null, [], null, false);
}
