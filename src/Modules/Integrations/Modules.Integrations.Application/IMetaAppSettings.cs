namespace Modules.Integrations.Application;

/// <summary>
/// Lo público de la app de Meta (spec 2026-10-09 §6.1): <c>appId</c> y <c>configId</c> viajan en la URL
/// del popup de Embedded Signup. <see cref="IsConfigured"/> es D-M3: sin la sección (sólo fuera de
/// Production), <c>whatsapp-cloud</c> no sale en el catálogo y el canje responde
/// <c>code_exchange_failed</c>. El secreto nunca pasa por acá.
/// </summary>
public interface IMetaAppSettings
{
    bool IsConfigured { get; }

    string? AppId { get; }

    string? ConfigId { get; }

    string GraphApiVersion { get; }
}
