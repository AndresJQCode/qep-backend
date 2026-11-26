namespace Modules.Scheduling.Domain;

/// <summary>
/// Quien atiende o lo que se reserva: un médico, un barbero, una silla, un salón. <see cref="DisplayName"/> es lo que ve el cliente.
/// <see cref="MemberId"/> es el miembro vinculado, opcional: define qué agenda ve quien no tiene «ver todas las agendas» (spec
/// 2026-11-21, D-7). No depende de RRHH.
/// </summary>
public sealed class Resource : ArchivableEntity
{
    private Resource()
    {
    }

    public ResourceId Id { get; private set; }

    public LocationId LocationId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public Guid? MemberId { get; private set; }

    public static Resource Create(
        ResourceId id, Guid tenantId, LocationId locationId, string displayName, Guid? memberId, DateTimeOffset now)
    {
        var resource = new Resource
        {
            Id = id,
            LocationId = locationId,
            DisplayName = RequireName(displayName),
            MemberId = memberId,
        };
        resource.Initialize(tenantId, now);
        return resource;
    }

    public void Update(string displayName, Guid? memberId, DateTimeOffset now)
    {
        DisplayName = RequireName(displayName);
        MemberId = memberId;
        Touch(now);
    }
}
