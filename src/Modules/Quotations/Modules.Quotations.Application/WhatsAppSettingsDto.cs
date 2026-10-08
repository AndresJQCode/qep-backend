using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La configuración de WhatsApp tal como la dibuja Configuración (spec 2026-10-07). Regla BFF:
/// <list type="bullet">
/// <item><c>ApiKeyConfigured</c> y <c>ApiKeyUpdatedAt</c>: el estado «configurada el 7 de
/// octubre» sin exponer la key (ni sus últimos caracteres, decisión 2).</item>
/// <item><c>ApiKeyReadable</c>: la key guardada se descifra de verdad (valor descartado en el
/// acto); <c>null</c> sin key. Detecta llave retirada <b>y</b> bytes que no descifran, para que la
/// pantalla pida volver a pegarla sin deducirlo de un envío fallido.</item>
/// <item><c>Modes</c> y <c>Providers</c>: colecciones fijas y completas; el select no conoce el
/// enum del backend.</item>
/// </list>
/// En Shared y Disabled viajan igual el proveedor, el número, la plantilla y el estado de la key:
/// son lo que la pantalla muestra al volver a «Cuenta propia».
/// </summary>
public sealed record WhatsAppSettingsDto(
    Guid TenantId,
    string Mode,
    string? Provider,
    bool ApiKeyConfigured,
    DateTimeOffset? ApiKeyUpdatedAt,
    bool? ApiKeyReadable,
    string? FromNumber,
    string? TemplateId,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Providers,
    long Version);

/// <summary>
/// Lo que el flujo de envío necesita saber antes de enviar (spec 2026-10-07). Aparte de
/// <see cref="WhatsAppSettingsDto"/> porque los roles son editables: quien puede enviar no
/// necesariamente puede leer Configuración. <c>Mode</c> viaja para que la pantalla sume la pista
/// «revisa la plantilla en Configuración» sólo cuando la cuenta es la propia.
/// </summary>
public sealed record WhatsAppChannelDto(bool Enabled, string Mode);

/// <summary>Los nombres exactos, como viajan: ordinal, sin minúsculas ni el número del miembro,
/// que es lo que <c>Enum.TryParse</c> sí aceptaría.</summary>
internal static class WhatsAppModes
{
    public static readonly IReadOnlyList<string> All =
        [nameof(WhatsAppMode.Shared), nameof(WhatsAppMode.Own), nameof(WhatsAppMode.Disabled)];

    public static readonly IReadOnlyList<string> Providers = [nameof(WhatsAppProvider.Zenvia)];

    public static bool TryParse(string? value, out WhatsAppMode mode)
    {
        foreach (var candidate in Enum.GetValues<WhatsAppMode>())
        {
            if (string.Equals(value, candidate.ToString(), StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }

        mode = default;
        return false;
    }

    public static bool IsProvider(string? value) =>
        string.Equals(value, nameof(WhatsAppProvider.Zenvia), StringComparison.Ordinal);
}

internal static class WhatsAppSettingsMappings
{
    public static WhatsAppSettingsDto ToDto(
        TenantWhatsAppSettings settings, IWhatsAppSecretProtector protector) =>
        new(
            settings.TenantId,
            settings.Mode.ToString(),
            settings.Provider?.ToString(),
            ApiKeyConfigured: settings.ApiToken is not null,
            settings.ApiTokenUpdatedAt,
            ApiKeyReadable: settings.ApiToken is null
                ? null
                // El valor descifrado se descarta en el acto: sólo sale el booleano.
                : protector.TryUnprotect(settings.TenantId, settings.ApiToken, out _),
            settings.FromNumber,
            settings.TemplateId,
            WhatsAppModes.All,
            WhatsAppModes.Providers,
            settings.Version);
}
