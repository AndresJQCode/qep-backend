using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Los ajustes del pedido viajan como documento entero (spec 2026-11-10 §6): un campo ausente no es un
/// <c>false</c> silencioso sino un 422 con la clave del campo, para que un frontend viejo no apague un ajuste
/// sin saberlo.
/// </summary>
public sealed class UpdateOrderSettingsValidatorTests
{
    [Fact]
    public void EachFieldIsRequired()
    {
        var result = new UpdateOrderSettingsValidator().Validate(
            new UpdateOrderSettingsCommand(Guid.CreateVersion7(), null, null, null));

        Assert.Equal(
            ["AutoArchive", "RequireFullPaymentToInvoice", "RequirePaymentProofToMarkPaid"],
            result.Errors.Select(error => error.PropertyName).Order().ToArray());
    }

    [Fact]
    public void AWholeDocumentIsValid() =>
        Assert.True(new UpdateOrderSettingsValidator().Validate(
            new UpdateOrderSettingsCommand(Guid.CreateVersion7(), false, false, false)).IsValid);
}
