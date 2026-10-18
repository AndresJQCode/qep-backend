namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.1: el client secret de la app con el que Shopify firma los webhooks. Puerto propio porque la configuración
/// de la app vive en Integrations (<c>IShopifyAppSettings</c>, tarea 5), que Shopify no puede referenciar; el adaptador está en
/// Bootstrapper. <c>null</c> fuera de Production sin el par configurado: entonces ninguna firma es válida. Nunca va a un DTO, un
/// log ni la auditoría.
/// </summary>
public interface IShopifyAppSecret
{
    string? ClientSecret { get; }
}
