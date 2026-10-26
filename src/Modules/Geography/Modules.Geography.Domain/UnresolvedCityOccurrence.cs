namespace Modules.Geography.Domain;

/// <summary>
/// Un pedido (y, si lo hay, el cliente) en el que apareció una ciudad sin resolver. Sólo lectura desde
/// EF: la escribe el SQL de la importación (SC-03), de ahí que no tenga constructor público ni
/// comportamiento.
/// </summary>
public sealed class UnresolvedCityOccurrence
{
    private UnresolvedCityOccurrence()
    {
    }

    public Guid Id { get; private set; }

    public UnresolvedCityId UnresolvedCityId { get; private set; }

    public Guid TenantId { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public string SourceRef { get; private set; } = string.Empty;

    public Guid OrderId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public bool PartyCityOnly { get; private set; }

    public bool CustomerCityOnly { get; private set; }

    public DateTimeOffset SeenAt { get; private set; }
}
