namespace Modules.Siigo.Domain;

/// <summary>
/// Spec 2026-11-20 (vendedores vinculados): el usuario de Siigo que factura como vendedor los pedidos de un asesor (una membresía de
/// QEP). Único por <c>(connection_id, membership_id)</c>; el mismo usuario puede estar en varios vínculos. <see cref="SiigoUserName"/>
/// es una foto del nombre al vincular, para mostrar un vínculo cuyo usuario ya no está activo en Siigo. Como entrada de
/// <c>SiigoSettings.ReplaceSellerLinks</c> sólo lleva la membresía y el usuario; la conexión y el tenant los pone la configuración.
/// </summary>
public sealed class SiigoSellerLink
{
    public const int UserIdMaxLength = 50;
    public const int UserNameMaxLength = 200;

    private SiigoSellerLink()
    {
    }

    public SiigoSellerLink(Guid membershipId, string siigoUserId, string siigoUserName)
    {
        MembershipId = membershipId;
        SiigoUserId = siigoUserId.Trim();
        SiigoUserName = FitName(siigoUserName, SiigoUserId);
    }

    public Guid ConnectionId { get; internal set; }

    public Guid TenantId { get; internal set; }

    public Guid MembershipId { get; private set; }

    public string SiigoUserId { get; private set; } = string.Empty;

    public string SiigoUserName { get; private set; } = string.Empty;

    internal void Relink(string siigoUserId, string siigoUserName)
    {
        SiigoUserId = siigoUserId.Trim();
        SiigoUserName = FitName(siigoUserName, SiigoUserId);
    }

    // El nombre viene de Siigo (o del navegador) y la columna mide 200: se recorta aquí para que ninguna ruta de
    // escritura, incluido el auto-link, reviente el guardado. Sin nombre se muestra el id, nunca una cadena vacía.
    private static string FitName(string name, string fallback)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return fallback;
        return trimmed.Length <= UserNameMaxLength ? trimmed : trimmed[..UserNameMaxLength].TrimEnd();
    }
}
