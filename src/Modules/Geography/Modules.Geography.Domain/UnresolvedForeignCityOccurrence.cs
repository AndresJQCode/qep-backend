namespace Modules.Geography.Domain;

/// <summary>
/// Un pedido en el que apareció una ciudad del exterior sin resolver. Sólo lectura desde EF: la escribe el SQL del reportero
/// (<c>UnresolvedForeignCityRepository</c>), de ahí que no tenga constructor público ni comportamiento. No guarda cliente:
/// la ocurrencia es del pedido.
/// </summary>
public sealed class UnresolvedForeignCityOccurrence
{
    private UnresolvedForeignCityOccurrence()
    {
    }

    public Guid Id { get; private set; }

    public UnresolvedForeignCityId UnresolvedCityId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public DateTimeOffset SeenAt { get; private set; }
}
