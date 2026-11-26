using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-11-21: la **única** fuente del nombre, idioma, categoría, texto base y ejemplos de la plantilla de factura, hermana de
/// <see cref="OrderTemplateDefinition"/>: mismo encabezado de documento (el PDF de la factura), sin pie ni botones. El admin puede editar
/// el texto al crearla, con las variables de <see cref="SystemTemplateVariables.Invoice"/>, y <see cref="BaseBody"/> es el que se usa si no
/// manda otro. El nombre va por versiones (<c>qep_factura</c>, <c>qep_factura_v2</c>…) por la misma razón que el pedido: Meta bloquea un
/// tiempo el nombre de una plantilla borrada. El sender lee el nombre vigente de la fila <c>Invoice</c>, no de una constante.
/// </summary>
public static class InvoiceTemplateDefinition
{
    public const string Name = MessageTemplateName.InvoiceName;
    public const string Language = "es";
    public const string Category = "UTILITY";
    public const string BaseBody = "Hola {{nombre_contacto}}, te enviamos la factura {{numero_factura}} por un total de {{valor_total}}. Adjuntamos el documento. ¡Gracias por tu compra!";
    public const string ExampleFileName = "factura-ejemplo.pdf";
    public const string ExampleMimeType = "application/pdf";

    // «qep_factura_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.InvoicePrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.Invoice.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_factura_v{n}</c>.</summary>
    public static string NameFor(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return version == 1 ? Name : VersionPrefix + version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>El inverso de <see cref="NameFor"/>, con las mismas reglas que el pedido: cualquier otro nombre es <c>null</c>.</summary>
    public static int? VersionOf(string? name)
    {
        if (string.Equals(name, Name, StringComparison.Ordinal))
        {
            return 1;
        }

        if (name is null || !name.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var digits = name[VersionPrefix.Length..];
        if (digits.Length == 0 || digits[0] == '0' || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 2 ? version : null;
    }

    /// <summary>Sin pie, como el pedido.</summary>
    public static TemplateDraft Draft(string name, string body) => new(name, Language, Category, null, body, null, [], Examples);

    public static JsonObject Payload(string handle, string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body), TemplateMediaHeader.Document(handle));

    /// <summary>La misma forma que exige el pedido (encabezado de documento, sin botones, cuerpo no vacío) pero con las variables de la
    /// factura. No hay claves viejas que traducir: es una plantilla nueva. Una con <c>vigencia</c> o <c>numero_pedido</c> no es una factura.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.Invoice);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason == TemplateUnsupportedReasons.MediaHeader
            && described.Parameters.All(allowed.Contains)
            && described.Buttons.Count == 0
            && OrderTemplateDefinition.IsDocumentHeader(componentsJson);
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();

    public static byte[] ExamplePdf() => MinimalPdf.Build("Factura de ejemplo - QEP");
}
