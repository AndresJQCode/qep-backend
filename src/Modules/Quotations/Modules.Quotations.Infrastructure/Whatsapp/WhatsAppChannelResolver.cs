using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Spec 2026-10-07: sin fila o <c>Shared</c> ⇒ el <see cref="IWhatsAppSender"/> global de hoy;
/// <c>Own</c> ⇒ un <see cref="ZenviaWhatsAppSender"/> armado por envío con la key descifrada, sobre
/// el <see cref="ZenviaHttpClient"/> compartido; <c>Disabled</c> ⇒ sin sender. <c>BaseUrl</c> sigue
/// siendo global. Scoped: lee por el DbContext del request.
/// </summary>
internal sealed class WhatsAppChannelResolver(
    ITenantWhatsAppSettingsRepository repository,
    IWhatsAppSecretProtector protector,
    IWhatsAppSender sharedSender,
    ZenviaHttpClient zenviaHttpClient,
    IOptions<QuotationsOptions> options,
    ILogger<ZenviaWhatsAppSender> senderLogger)
    : IWhatsAppChannelResolver
{
    public async Task<WhatsAppChannel> ResolveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var settings = await repository.FindReadOnlyAsync(tenantId, cancellationToken);
        if (settings is null || settings.Mode == WhatsAppMode.Shared)
        {
            return new WhatsAppChannel(WhatsAppMode.Shared, sharedSender);
        }

        if (settings.Mode == WhatsAppMode.Disabled)
        {
            return new WhatsAppChannel(WhatsAppMode.Disabled, null);
        }

        return new WhatsAppChannel(WhatsAppMode.Own, OwnSender(settings));
    }

    private ZenviaWhatsAppSender OwnSender(TenantWhatsAppSettings settings)
    {
        string token;
        try
        {
            // El CHECK own_complete garantiza la key en Own; el null se trata igual que ilegible.
            var secret = settings.ApiToken ?? throw new CryptographicException("No API key is stored.");
            token = protector.Unprotect(settings.TenantId, secret);
        }
        catch (Exception exception) when (exception is InvalidOperationException or CryptographicException)
        {
            // Código propio y no el genérico quotation.send.failed: es el único que la pantalla puede
            // convertir en "pide que la revisen en Configuración". El mensaje de la interna nombra
            // la clave de configuración, nunca un valor.
            throw new QuotationsDomainException(
                "quotation.whatsapp.settings_unreadable",
                "The tenant's WhatsApp API key cannot be decrypted with the configured keys.",
                exception);
        }

        // Los `!` de FromNumber y TemplateId son seguros por el mismo CHECK
        // (CK_tenant_whatsapp_settings_own_complete): en modo Own ambos son NOT NULL.
        return new ZenviaWhatsAppSender(
            zenviaHttpClient.Client,
            new ZenviaSenderSettings(
                token,
                settings.FromNumber!,
                settings.TemplateId!,
                options.Value.WhatsApp.BaseUrl,
                ZenviaAccount.Tenant),
            senderLogger);
    }
}
