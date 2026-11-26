namespace Modules.Wholesale.Application;

/// <summary>Acciones de auditoría del módulo (D-24), todas en el tenant del mayorista: una por escritura.</summary>
public static class WholesaleAuditActions
{



    public const string DistributorInvited = "wholesale.distributor.invited";

    public const string DistributorUpdated = "wholesale.distributor.updated";

    public const string DistributorInvitationResent = "wholesale.distributor.invitation_resent";

    /// <summary>Spec 2026-11-19 §1: «Copiar» rotó el link. El campo es <c>invitation</c>; ni el token ni su hash.</summary>
    public const string DistributorInvitationRotated = "wholesale.distributor.invitation_rotated";

    public const string DistributorBlocked = "wholesale.distributor.blocked";

    public const string DistributorUnblocked = "wholesale.distributor.unblocked";

    public const string DistributorDeleted = "wholesale.distributor.deleted";

    public const string DistributorAccepted = "wholesale.distributor.accepted";

    /// <summary>Spec 2026-11-19 §6: el correo cambió al aceptar. El campo es <c>email</c>; los correos no van en la auditoría.</summary>
    public const string DistributorEmailChanged = "wholesale.distributor.email_changed";

    public static class Resources
    {

        public const string Distributor = "distributor";
    }
}
