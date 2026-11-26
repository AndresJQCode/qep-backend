using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modules.Support.Application;
using Modules.Support.Infrastructure.Pdf;

namespace Modules.Support.UnitTests;

/// <summary>
/// Fase 4b (H12): <c>formal-reply.typ</c> viaja embebida en el ensamblado y lee los datos de <see cref="SupportFormalReplyDocument"/>
/// serializados en camelCase. Renombrar una propiedad compila y rompe el PDF sólo en producción, con un 400 de Typst; esta prueba lo
/// pone en rojo acá: cada <c>data.&lt;nombre&gt;</c> de la plantilla tiene que existir en el contrato.
/// </summary>
public sealed partial class SupportFormalReplyTemplateTests
{
    // Una cadena de accesos (data.letterhead.companyName) sin el método final de Typst (data.decision.split(…)).
    [GeneratedRegex(@"\bdata((?:\.[A-Za-z][A-Za-z0-9]*\b(?!\())+)", RegexOptions.CultureInvariant)]
    private static partial Regex DataAccess();

    [Fact]
    public void TheTemplateIsEmbedded()
    {
        var template = SupportPdfRenderer.Template;

        Assert.False(string.IsNullOrWhiteSpace(template));
        Assert.Contains("json(sys.inputs.data)", template, StringComparison.Ordinal);
        // Nada de la plantilla evalúa datos como marcado: un texto del solicitante con «#» o «[» se imprime tal cual.
        Assert.DoesNotContain("eval(", template, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTemplateReadsOnlyFieldsOfTheDocument()
    {
        var accesses = DataAccess().Matches(SupportPdfRenderer.Template)
            .Select(match => match.Groups[1].Value.TrimStart('.').Split('.'))
            .ToList();

        Assert.NotEmpty(accesses);
        foreach (var chain in accesses)
        {
            var type = typeof(SupportFormalReplyDocument);
            foreach (var segment in chain)
            {
                var property = PropertyOf(type, segment);
                Assert.True(property is not null, $"data.{string.Join('.', chain)}: '{segment}' is not a property of {type.Name}.");
                type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            }
        }
    }

    // El contrato con la plantilla es la serialización web (camelCase) que usa el renderer.
    private static PropertyInfo? PropertyOf(Type type, string camelCaseName) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name) == camelCaseName);
}
