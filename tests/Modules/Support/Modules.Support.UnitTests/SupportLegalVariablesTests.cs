using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Las variables de los documentos legales del portal (spec 2026-11-16, D-F8, V-1 y V-2).
public sealed class SupportLegalVariablesTests
{
    private static readonly SupportLegalVariableValues Values = new("Comercial Andina", "Comercial Andina S.A.S.", "NIT", "900123456-7");

    private static readonly SupportLegalVariableValues WithoutOrganization = new("Comercial Andina", null, null, null);

    [Fact]
    public void TheListIsTheContractWithTheConsole()
    {
        Assert.Equal(
            [
                ("empresa", "Empresa que responde", "Comercial Andina S.A.S."),
                ("tipo_documento", "Tipo de documento de la empresa", "NIT"),
                ("documento_identificacion", "Número de documento de la empresa", "900123456-7"),
                ("nombre_comercial", "Nombre del negocio", "Comercial Andina"),
            ],
            SupportLegalVariables.All.Select(variable => (variable.Name, variable.Label, variable.Example)));
    }

    [Theory]
    [InlineData("<p>{{empresa}}</p>")]
    [InlineData("<p>{{ empresa }} y {{tipo_documento}} {{documento_identificacion}} de {{  nombre_comercial }}</p>")]
    [InlineData("<p>Sin variables.</p>")]
    [InlineData("<p>Una llave { suelta } y }} al revés {{</p>")]
    [InlineData(null)]
    public void KnownTokensAreNotUnknown(string? text) => Assert.Empty(SupportLegalVariables.UnknownIn(text));

    [Theory]
    [InlineData("<p>{{ciudad}}</p>", "ciudad")]
    [InlineData("<p>{{Empresa}}</p>", "Empresa")]
    [InlineData("<p>{{}}</p>", "")]
    [InlineData("<p>{{ empresa_x }}</p>", "empresa_x")]
    [InlineData("<p>{{nombre comercial}}</p>", "nombre comercial")]
    public void AnythingElseBetweenBracesIsUnknown(string text, string unknown) =>
        Assert.Equal([unknown], SupportLegalVariables.UnknownIn(text));

    [Fact]
    public void UnknownNamesComeOncePerName() =>
        Assert.Equal(["ciudad", "pais"], SupportLegalVariables.UnknownIn("{{ciudad}} {{empresa}} {{pais}} {{ ciudad }}"));

    [Fact]
    public void TheHtmlGetsTheValues() =>
        Assert.Equal(
            "<p>Comercial Andina S.A.S. (NIT 900123456-7) atiende a los clientes de Comercial Andina.</p>",
            SupportLegalVariables.FillHtml(
                "<p>{{empresa}} ({{ tipo_documento }} {{documento_identificacion}}) atiende a los clientes de {{nombre_comercial}}.</p>",
                Values));

    // Review Focus 2: una razón social con & o < no rompe ni inyecta marcado en el HTML.
    [Fact]
    public void TheHtmlGetsTheValuesEncoded()
    {
        var values = new SupportLegalVariableValues("Pan & Café <b>", "Verde & Cía <S.A.S.>", "NIT", "\"900\"'1");

        Assert.Equal(
            "<p>Verde &amp; Cía &lt;S.A.S.&gt; · &quot;900&quot;&#39;1 · Pan &amp; Café &lt;b&gt;</p>",
            SupportLegalVariables.FillHtml("<p>{{empresa}} · {{documento_identificacion}} · {{nombre_comercial}}</p>", values));
    }

    // V-2: la pregunta es texto plano y el portal la pinta como texto, así que va tal cual.
    [Fact]
    public void TheTextGetsTheValuesAsTheyAre()
    {
        var values = new SupportLegalVariableValues("Pan & Café", "Verde & Cía <S.A.S.>", "NIT", "900");

        Assert.Equal(
            "¿Autorizas a Verde & Cía <S.A.S.> (NIT 900), dueña de Pan & Café?",
            SupportLegalVariables.FillText("¿Autorizas a {{empresa}} ({{tipo_documento}} {{documento_identificacion}}), dueña de {{nombre_comercial}}?", values));
    }

    [Fact]
    public void WithoutOrganizationItsTokensCannotBeFilled()
    {
        Assert.Null(SupportLegalVariables.FillHtml("<p>{{empresa}}</p>", WithoutOrganization));
        Assert.Null(SupportLegalVariables.FillHtml("<p>{{tipo_documento}}</p>", WithoutOrganization));
        Assert.Null(SupportLegalVariables.FillText("{{documento_identificacion}}", WithoutOrganization));
        Assert.Equal("<p>Comercial Andina</p>", SupportLegalVariables.FillHtml("<p>{{nombre_comercial}}</p>", WithoutOrganization));
        Assert.Equal("<p>Sin variables.</p>", SupportLegalVariables.FillHtml("<p>Sin variables.</p>", WithoutOrganization));
    }

    // Un token desconocido sólo llega a servirse desde una fila escrita a mano: se deja como está.
    [Fact]
    public void AnUnknownTokenStaysAsItIs() =>
        Assert.Equal(
            "<p>{{ciudad}} {{Empresa}} Comercial Andina S.A.S.</p>",
            SupportLegalVariables.FillHtml("<p>{{ciudad}} {{Empresa}} {{empresa}}</p>", Values));
}
