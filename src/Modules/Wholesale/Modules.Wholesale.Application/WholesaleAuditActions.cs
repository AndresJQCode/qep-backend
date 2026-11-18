namespace Modules.Wholesale.Application;

/// <summary>Acciones de auditoría del módulo (D-24), todas en el tenant del mayorista: una por escritura.</summary>
public static class WholesaleAuditActions
{
    public const string PriceListCreated = "wholesale.price_list.created";

    public const string PriceListUpdated = "wholesale.price_list.updated";

    public const string PriceListDeleted = "wholesale.price_list.deleted";

    public const string DistributorInvited = "wholesale.distributor.invited";

    public const string DistributorUpdated = "wholesale.distributor.updated";

    public const string DistributorInvitationResent = "wholesale.distributor.invitation_resent";

    public const string DistributorBlocked = "wholesale.distributor.blocked";

    public const string DistributorUnblocked = "wholesale.distributor.unblocked";

    public const string DistributorDeleted = "wholesale.distributor.deleted";

    public const string DistributorAccepted = "wholesale.distributor.accepted";

    public static class Resources
    {
        public const string PriceList = "price_list";

        public const string Distributor = "distributor";
    }
}
