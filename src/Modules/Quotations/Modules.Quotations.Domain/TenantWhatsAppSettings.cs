namespace Modules.Quotations.Domain;

/// <summary>Qué clase de cambio dejó un guardado. Todo en falso es un no-op: no sube versión ni
/// se audita (spec 2026-10-07, «Domain»).</summary>
public sealed record WhatsAppSettingsChanges(
    bool ModeChanged,
    bool ApiKeyReplaced,
    bool DetailsChanged,
    bool KeyRotated)
{
    public bool Any => ModeChanged || ApiKeyReplaced || DetailsChanged || KeyRotated;
}

/// <summary>
/// La configuración de WhatsApp de un tenant (spec 2026-10-07). Una fila por tenant, PK
/// <see cref="TenantId"/>.
///
/// Cambiar a <see cref="WhatsAppMode.Shared"/> o <see cref="WhatsAppMode.Disabled"/>
/// <b>conserva</b> la cuenta propia (decisión 8 del spec): volver a <see cref="WhatsAppMode.Own"/>
/// no obliga a pegar de nuevo la API key.
/// </summary>
public sealed class TenantWhatsAppSettings
{
    public const long DefaultVersion = 1;
    public const int FromNumberMaxLength = 15;
    public const int TemplateIdMaxLength = 64;

    // EF Core materializa por acá.
    private TenantWhatsAppSettings()
    {
    }

    private TenantWhatsAppSettings(Guid tenantId, DateTimeOffset now)
    {
        TenantId = tenantId;
        Mode = WhatsAppMode.Shared;
        Version = DefaultVersion;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }

    public WhatsAppMode Mode { get; private set; }

    public WhatsAppProvider? Provider { get; private set; }

    public ProtectedSecret? ApiToken { get; private set; }

    public DateTimeOffset? ApiTokenUpdatedAt { get; private set; }

    public string? FromNumber { get; private set; }

    public string? TemplateId { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>En memoria y en versión 1, para que el primer PUT pase por el mismo chequeo de
    /// versión que los demás (mismo criterio que <c>OrdersExportLayout.CreateDefault</c>).</summary>
    public static TenantWhatsAppSettings CreateEmpty(Guid tenantId, DateTimeOffset now) =>
        new(tenantId, now);

    /// <summary>
    /// Aplica un guardado. Los nulos <b>conservan</b> lo guardado; con valor, lo reemplazan.
    /// <paramref name="newToken"/> siempre cuenta como reemplazo (el dominio no ve el texto) y mueve
    /// <see cref="ApiTokenUpdatedAt"/>; <paramref name="rekeyedToken"/> es la misma key cifrada con
    /// otra llave y no lo mueve. Con cualquier cambio la versión sube una sola vez.
    /// </summary>
    public WhatsAppSettingsChanges Configure(
        WhatsAppMode mode,
        WhatsAppProvider? provider,
        ProtectedSecret? newToken,
        ProtectedSecret? rekeyedToken,
        string? fromNumber,
        string? templateId,
        DateTimeOffset now)
    {
        if (newToken is not null && rekeyedToken is not null)
        {
            throw new ArgumentException(
                "A new token and a re-encrypted one cannot be applied in the same save.",
                nameof(rekeyedToken));
        }

        var nextProvider = provider ?? Provider;
        var nextToken = newToken ?? rekeyedToken ?? ApiToken;
        var nextFromNumber = fromNumber ?? FromNumber;
        var nextTemplateId = templateId ?? TemplateId;

        if (mode == WhatsAppMode.Own &&
            (nextProvider is null || nextToken is null || nextFromNumber is null || nextTemplateId is null))
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp_settings.incomplete",
                "The own WhatsApp account needs a provider, an API key, a sender number and a template.");
        }

        var changes = new WhatsAppSettingsChanges(
            ModeChanged: mode != Mode,
            ApiKeyReplaced: newToken is not null,
            DetailsChanged: nextProvider != Provider
                || !string.Equals(nextFromNumber, FromNumber, StringComparison.Ordinal)
                || !string.Equals(nextTemplateId, TemplateId, StringComparison.Ordinal),
            KeyRotated: rekeyedToken is not null);

        if (!changes.Any)
        {
            return changes;
        }

        Mode = mode;
        Provider = nextProvider;
        FromNumber = nextFromNumber;
        TemplateId = nextTemplateId;
        if (newToken is not null)
        {
            ApiToken = newToken;
            ApiTokenUpdatedAt = now;
        }
        else if (rekeyedToken is not null)
        {
            ApiToken = rekeyedToken;
        }

        Version++;
        UpdatedAt = now;
        return changes;
    }

    /// <summary>Lo mismo que <c>rekeyedToken</c> para quien re-cifra sin un PUT
    /// (<c>WhatsAppTokenRekeyWorker</c>). Sin key guardada no hay nada que re-cifrar.</summary>
    public bool Reprotect(ProtectedSecret rekeyed, DateTimeOffset now)
    {
        if (ApiToken is null)
        {
            return false;
        }

        ApiToken = rekeyed;
        Version++;
        UpdatedAt = now;
        return true;
    }
}
