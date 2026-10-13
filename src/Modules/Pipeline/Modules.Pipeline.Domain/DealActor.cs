namespace Modules.Pipeline.Domain;

/// <summary>Spec 2026-10-13 D19: quién hizo el cambio. Una persona lleva su membresía; el sistema (el worker de cotizaciones),
/// ninguna.</summary>
public readonly record struct DealActor(ActorKind Kind, Guid? MemberId)
{
    public static DealActor ForSystem { get; } = new(ActorKind.System, null);

    public static DealActor ForUser(Guid memberId) => new(ActorKind.User, memberId);
}
