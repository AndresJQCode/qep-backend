namespace Modules.Wholesale.Domain;

/// <summary>El estado de la relación con un distribuidor. El vencimiento de la invitación es perezoso: no hay estado
/// <c>Expired</c>; una invitación vencida sigue <see cref="Invited"/> y se puede reenviar o borrar.</summary>
public enum DistributorStatus
{
    Invited,
    Active,
    Blocked,
}
