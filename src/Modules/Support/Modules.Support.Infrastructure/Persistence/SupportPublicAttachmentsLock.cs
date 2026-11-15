namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Block E (E-4) del formulario público: el candado por tenant que serializa abrir una subida anónima. Contar las abiertas sin reclamar y
/// abrir la nueva van con él tomado, así dos aperturas a la vez en el borde hacen fila y el tope
/// (<c>SupportPublicAttachmentRules.MaxPendingPerTenantPerDay</c>) es exacto. Va en la forma de dos claves,
/// <c>pg_advisory_xact_lock(Namespace, hashtext(tenant_id::text))</c>, y se suelta con el commit o el rollback de la transacción de Soporte.
/// El número no lo usa ningún otro candado de dos claves (<c>TenantChangeLock</c> = 20261008, <c>AutomationsConversationLock</c> =
/// 20261022, <c>ShopifyTokenLock</c> = 20261112, <c>ConnectionSlotsLock</c> y <c>StartupLock</c> = 20261113, <c>InstagramTokenLock</c> =
/// 20261114; estos dos últimos llegaron a <c>develop</c> después de que esta rama saliera).
/// </summary>
internal static class SupportPublicAttachmentsLock
{
    public const int Namespace = 20261115;
}
