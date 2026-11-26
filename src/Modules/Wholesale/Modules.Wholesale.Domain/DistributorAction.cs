namespace Modules.Wholesale.Domain;

/// <summary>Lo que la pantalla puede ofrecer sobre un distribuidor (<c>allowedActions</c>), en el orden del contrato.</summary>
public enum DistributorAction
{
    Edit,
    Resend,
    Delete,
    Block,
    Unblock,
}
