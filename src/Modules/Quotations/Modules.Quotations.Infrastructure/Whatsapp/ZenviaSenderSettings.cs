namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>De quién es la cuenta de Zenvia (spec 2026-10-07): cambia cómo se reportan las
/// fallas, no cómo se envía.</summary>
internal enum ZenviaAccount
{
    /// <summary>La cuenta global de QEP (modo <c>Shared</c>).</summary>
    Qep,

    /// <summary>La cuenta propia del tenant (modo <c>Own</c>).</summary>
    Tenant
}

/// <summary>
/// Con qué cuenta sale un envío. <see cref="ToString"/> esconde el token: es un record que lo
/// lleva en claro, y el de un record imprime todas sus propiedades (spec 2026-10-07).
/// </summary>
internal sealed record ZenviaSenderSettings(
    string ApiToken,
    string FromNumber,
    string TemplateId,
    string BaseUrl,
    ZenviaAccount Account)
{
    public static ZenviaSenderSettings ForQep(WhatsAppOptions options) =>
        new(options.ApiToken, options.FromNumber, options.TemplateId, options.BaseUrl, ZenviaAccount.Qep);

    public override string ToString() =>
        $"ZenviaSenderSettings {{ ApiToken = ***, FromNumber = {FromNumber}, TemplateId = {TemplateId}, "
        + $"BaseUrl = {BaseUrl}, Account = {Account} }}";
}
