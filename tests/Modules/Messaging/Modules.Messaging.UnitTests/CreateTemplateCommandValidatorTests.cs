using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.8: cada campo del cuerpo de crear una plantilla libre con su clave en <c>errors</c>. Lo que Meta valida y
/// nosotros no (proporción de variables, variable al final) no está acá.</summary>
public sealed class CreateTemplateCommandValidatorTests
{
    private static readonly CreateTemplateCommandValidator Validator = new();

    private static CreateTemplateCommand Valid(
        string? name = "promo_octubre", string? language = "es", string? category = "MARKETING", string? header = "Oferta",
        string? body = "Hola {{first_name}}, {{product}} con descuento.", string? footer = "QEP",
        IReadOnlyList<TemplateButtonInput>? buttons = null, IReadOnlyDictionary<string, string?>? examples = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), name, language, category, header, body, footer,
            buttons ?? [new TemplateButtonInput("QuickReply", "Detener", null, null)],
            examples ?? new Dictionary<string, string?> { ["first_name"] = "Ana", ["product"] = "Silla" });

    private static string[] Keys(CreateTemplateCommand command) =>
        Validator.Validate(command).Errors.Select(error => error.PropertyName).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void AValidCommandHasNoErrors() => Assert.Empty(Keys(Valid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Promo Octubre")]
    [InlineData("promo-octubre")]
    [InlineData("qep_cotizacion")]
    [InlineData("qep_cotizacion_v2")]
    public void TheNameIsLowercaseDigitsUnderscoresAndNotReserved(string? name) => Assert.Equal(["name"], Keys(Valid(name: name)));

    [Theory]
    [InlineData(null)]
    [InlineData("ES")]
    [InlineData("es-CO")]
    [InlineData("español")]
    public void TheLanguageHasTheShapeEsOrEsCo(string? language) => Assert.Equal(["language"], Keys(Valid(language: language)));

    [Theory]
    [InlineData("es")]
    [InlineData("es_CO")]
    [InlineData("en_US")]
    public void TheseLanguagesAreValid(string language) => Assert.Empty(Keys(Valid(language: language)));

    [Theory]
    [InlineData(null)]
    [InlineData("AUTHENTICATION")]
    [InlineData("marketing")]
    public void TheCategoryIsMarketingOrUtility(string? category) => Assert.Equal(["category"], Keys(Valid(category: category)));

    [Fact]
    public void TheHeaderIsShortAndHasNoVariables()
    {
        Assert.Equal(["header"], Keys(Valid(header: new string('a', 61))));
        Assert.Equal(["header"], Keys(Valid(header: "Hola {{first_name}}")));
        Assert.Empty(Keys(Valid(header: null)));
    }

    [Fact]
    public void TheFooterIsShort() => Assert.Equal(["footer"], Keys(Valid(footer: new string('a', 61))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheBodyIsRequired(string? body) => Assert.Equal(["body"], Keys(Valid(body: body, examples: new Dictionary<string, string?>())));

    [Fact]
    public void TheBodyIsAtMost1024() => Assert.Equal(["body"], Keys(Valid(body: new string('a', 1025), examples: new Dictionary<string, string?>())));

    [Fact]
    public void ABodyMadeOnlyOfVariablesIsRejected() =>
        Assert.Equal(["body"], Keys(Valid(body: "{{first_name}}{{product}}")));

    // Review Focus 4: Meta rechaza variables con mayúsculas o espacios; se marca en body antes de llegar a Meta.
    [Theory]
    [InlineData("Hola {{ Nombre }}")]
    [InlineData("Hola {{first name}}")]
    [InlineData("Hola {{First_Name}}")]
    public void AVariableWithUppercaseOrSpacesIsRejectedInBody(string body) =>
        Assert.Contains("body", Keys(Valid(body: body, examples: new Dictionary<string, string?>())));

    [Fact]
    public void ExamplesMustMatchTheVariablesExactly()
    {
        Assert.Equal(["examples"], Keys(Valid(examples: new Dictionary<string, string?> { ["first_name"] = "Ana" })));
        Assert.Equal(["examples"], Keys(Valid(examples: new Dictionary<string, string?> { ["first_name"] = "Ana", ["product"] = "Silla", ["extra"] = "x" })));
        Assert.Equal(["examples"], Keys(Valid(examples: new Dictionary<string, string?> { ["first_name"] = "Ana", ["product"] = "" })));
        // Valid(examples: null) caería en el valor por defecto del ayudante: el null se fuerza con «with».
        Assert.Equal(["examples"], Keys(Valid() with { Examples = null }));
    }

    [Fact]
    public void AtMostTenButtonsEachWithAShortText()
    {
        var eleven = Enumerable.Range(0, 11).Select(index => new TemplateButtonInput("QuickReply", $"b{index}", null, null)).ToArray();
        Assert.Equal(["buttons"], Keys(Valid(buttons: eleven)));
        Assert.Equal(["buttons[0].text"], Keys(Valid(buttons: [new TemplateButtonInput("QuickReply", new string('a', 26), null, null)])));
        Assert.Equal(["buttons[0].text"], Keys(Valid(buttons: [new TemplateButtonInput("QuickReply", "", null, null)])));
        Assert.Equal(["buttons[0].type"], Keys(Valid(buttons: [new TemplateButtonInput("Flow", "x", null, null)])));
    }

    [Fact]
    public void AUrlButtonNeedsAnAbsoluteHttpsUrlAndAPhoneButtonAPhone()
    {
        Assert.Equal(["buttons[0].url"], Keys(Valid(buttons: [new TemplateButtonInput("Url", "Ver", "http://ejemplo.co", null)])));
        Assert.Equal(["buttons[0].url"], Keys(Valid(buttons: [new TemplateButtonInput("Url", "Ver", null, null)])));
        Assert.Equal(["buttons[0].url"], Keys(Valid(buttons: [new TemplateButtonInput("Url", "Ver", "https://ejemplo.co/{{1}}", null)])));
        Assert.Equal(["buttons[0].phoneNumber"], Keys(Valid(buttons: [new TemplateButtonInput("PhoneNumber", "Llamar", null, "300 123")])));
        Assert.Empty(Keys(Valid(buttons: [new TemplateButtonInput("PhoneNumber", "Llamar", null, "+573001234567")])));
    }

    [Fact]
    public void QuickRepliesAreContiguousAtTheStartOrTheEnd()
    {
        static TemplateButtonInput Quick(string text) => new("QuickReply", text, null, null);
        static TemplateButtonInput Link(string text) => new("Url", text, "https://ejemplo.co", null);

        Assert.Empty(Keys(Valid(buttons: [Quick("a"), Quick("b"), Link("c")])));
        Assert.Empty(Keys(Valid(buttons: [Link("a"), Quick("b"), Quick("c")])));
        Assert.Equal(["buttons"], Keys(Valid(buttons: [Quick("a"), Link("b"), Quick("c")])));
    }

    [Fact]
    public void ErrorsAreInSpanishAndTutean()
    {
        var error = Validator.Validate(Valid(name: "Promo")).Errors.Single();
        Assert.Equal(TemplateMessages.Name, error.ErrorMessage);
        Assert.Contains("minúsculas", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qep_cotizacion")]
    [InlineData("qep_cotizacion_v2")]
    public void AReservedNameGetsTheReservedMessage(string name)
    {
        var error = Validator.Validate(Valid(name: name)).Errors.Single();
        Assert.Equal(TemplateMessages.NameReserved, error.ErrorMessage);
        Assert.Equal("El nombre no puede ser «qep_cotizacion» ni empezar por «qep_cotizacion_»: está reservado para la plantilla de cotización.", error.ErrorMessage);
    }

    [Fact]
    public void ANameThatOnlyStartsWithTheReservedWordIsNotReserved() => Assert.Empty(Keys(Valid(name: "qep_cotizaciones")));
}
