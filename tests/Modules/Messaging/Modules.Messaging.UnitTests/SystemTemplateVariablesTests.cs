using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

public sealed class SystemTemplateVariablesTests
{
    [Fact]
    public void EachKindHasItsClosedListInOrder()
    {
        string[] fixedKeys = ["nombre_contacto", "asesor", "empresa", "saludo"];
        Assert.Equal([.. fixedKeys, "numero_cotizacion", "valor_total", "vigencia"], SystemTemplateVariables.Quotation.Select(v => v.Key));
        Assert.Equal([.. fixedKeys, "servicio", "valor", "vencimiento"], SystemTemplateVariables.BillingReminder.Select(v => v.Key));
        Assert.Equal([.. fixedKeys, "cantidad_cobros", "total_vencido"], SystemTemplateVariables.BillingOverdue.Select(v => v.Key));
        Assert.Equal(
            ["Nombre del contacto", "Nombre del asesor", "Nombre de la empresa", "Saludo según la hora", "Número de cotización", "Valor total", "Vigencia"],
            SystemTemplateVariables.Quotation.Select(v => v.Label));
        Assert.Empty(SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation).Intersect(SystemTemplateVariables.LegacyKeys.Keys));
        Assert.All(SystemTemplateVariables.LegacyKeys.Values, key => Assert.Contains(key, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation)));
    }

    [Theory]
    [InlineData("Hola {{nombre_contacto}}, gracias.", null)]
    [InlineData("Hola {{otra}}, gracias.", TemplateMessages.BodyVariableNotAllowed)]
    [InlineData("Hola {{ nombre_contacto }}, gracias.", TemplateMessages.BodyVariable)]
    [InlineData("{{nombre_contacto}}", TemplateMessages.Body)]
    [InlineData("   ", TemplateMessages.Body)]
    public void TheBodyRuleChecksShapeThenTheList(string body, string? expected) =>
        Assert.Equal(expected, TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Quotation)));

    [Fact]
    public void WithoutAListAnyWellFormedVariablePasses() => Assert.Null(TemplateBodyRules.Check("Hola {{otra}}, gracias."));

    [Fact]
    public void ABodyOverTheLimitIsRejected() =>
        Assert.Equal(TemplateMessages.Body, TemplateBodyRules.Check(new string('a', 1025)));
}
