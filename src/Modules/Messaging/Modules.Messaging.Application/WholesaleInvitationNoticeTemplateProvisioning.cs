using System.Globalization;
using System.Text.Json.Nodes;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Mayorista: la **única** fuente del nombre, idioma, categoría, texto base y ejemplos de la plantilla del aviso por WhatsApp de una
/// invitación de distribuidor, calcada de <see cref="SupportReplyTemplateDefinition"/>: sólo cuerpo, sin encabezado ni botones, y sin
/// vencimiento. El aviso dice a qué correo salió la invitación; el link y el token nunca viajan por WhatsApp. El admin puede editar el texto
/// al crearla, con las variables de <see cref="SystemTemplateVariables.WholesaleInvitationNotice"/>. El nombre va por versiones
/// (<c>qep_mayorista_invitacion</c>, <c>qep_mayorista_invitacion_v2</c>…) porque Meta bloquea un tiempo el nombre de una plantilla borrada.
/// El envío lee el nombre vigente de la fila <c>WholesaleInvitationNotice</c>, no de esta constante.
/// </summary>
public static class WholesaleInvitationNoticeTemplateDefinition
{
    public const string Name = MessageTemplateName.WholesaleInvitationNoticeName;
    public const string Language = "es";
    public const string Category = "UTILITY";

    /// <summary>El texto aprobado por el owner. No empieza ni termina con una variable: Meta rechaza un cuerpo así.</summary>
    public const string BaseBody =
        "¡Hola, {{nombre_contacto}}! {{mayorista}} te invitó como distribuidor en la plataforma Lulo CRM. Te enviamos la invitación al "
        + "correo {{correo}}. Si no la encuentras en tu bandeja de entrada, revisa la carpeta de spam o correo no deseado.";

    /// <summary>Las claves que el envío llena además de las fijas: el nombre del tenant que invita y el correo completo de la invitación.</summary>
    public const string WholesalerKey = "mayorista";

    public const string EmailKey = "correo";

    // «qep_mayorista_invitacion_v»: dentro del prefijo reservado, así que ninguna libre puede tomar una versión.
    private const string VersionPrefix = MessageTemplateName.WholesaleInvitationNoticePrefix + "v";

    public static readonly IReadOnlyDictionary<string, string> Examples =
        SystemTemplateVariables.WholesaleInvitationNotice.ToDictionary(variable => variable.Key, variable => variable.Example, StringComparer.Ordinal);

    /// <summary>El nombre de la versión <paramref name="version"/>: la 1 es <see cref="Name"/>; de la 2 en adelante, <c>qep_mayorista_invitacion_v{n}</c>.</summary>
    public static string NameFor(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return version == 1 ? Name : VersionPrefix + version.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>El inverso de <see cref="NameFor"/>, con las mismas reglas que la de pedido: cualquier otro nombre es <c>null</c>.</summary>
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

    /// <summary>Sólo cuerpo: sin encabezado, pie ni botones.</summary>
    public static TemplateDraft Draft(string name, string body) => new(name, Language, Category, null, body, null, [], Examples);

    public static JsonObject Payload(string name, string body) => TemplateComponentsBuilder.CreatePayload(Draft(name, body));

    /// <summary>Una externa se adopta si tiene sólo cuerpo (sin encabezado de ningún tipo ni botones), no vacío, y sus variables son todas
    /// de la lista del aviso. A diferencia de Soporte no se admite ninguna clave vieja: esta plantilla nació con las nuevas y el envío no
    /// sabe llenar otra. Mira la forma, no el texto.</summary>
    public static bool IsAdoptable(string componentsJson)
    {
        ArgumentNullException.ThrowIfNull(componentsJson);
        var described = TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson);
        var allowed = SystemTemplateVariables.KeysOf(SystemTemplateVariables.WholesaleInvitationNotice);
        return !string.IsNullOrWhiteSpace(described.Body)
            && described.Header is null
            && described.UnsupportedReason is null
            && described.Parameters.All(allowed.Contains)
            && described.Buttons.Count == 0;
    }

    internal static string? StoredBody(string componentsJson) =>
        TemplateCatalog.FromStored(Name, Language, Category, TemplateComponentsBuilder.Named, componentsJson).Body?.Trim();
}
