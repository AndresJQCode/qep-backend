namespace Modules.Wholesale.Domain;

/// <summary>
/// Códigos estables de error del módulo («Códigos nuevos» del contrato de mayorista). El mapeo a HTTP vive en
/// <c>ApiExceptionHandler</c> y en los handlers; acá sólo el contrato.
/// </summary>
public static class WholesaleErrorCodes
{
    // 404
    public const string PriceListNotFound = "wholesale.price_list.not_found";

    public const string DistributorNotFound = "wholesale.distributor.not_found";

    public const string InvitationNotFound = "wholesale.invitation.not_found";

    // 403
    public const string InvitationEmailMismatch = "wholesale.invitation.email_mismatch";

    public const string InvitationNotTenantOwner = "wholesale.invitation.not_tenant_owner";

    // 409
    public const string PriceListInUse = "wholesale.price_list.in_use";

    public const string PriceListNameTaken = "wholesale.price_list.name_taken";

    public const string DistributorIdentificationTaken = "wholesale.distributor.identification_taken";

    public const string DistributorInvalidTransition = "wholesale.distributor.invalid_transition";

    public const string DistributorAlreadyLinked = "wholesale.distributor.already_linked";

    public const string InvitationAlreadyAccepted = "wholesale.invitation.already_accepted";

    public const string InvitationUnavailable = "wholesale.invitation.unavailable";

    // 410
    public const string InvitationExpired = "wholesale.invitation.expired";

    // 422
    public const string PriceListCategoryNotFound = "wholesale.price_list.category_not_found";

    public const string DistributorAdvisorInvalid = "wholesale.distributor.advisor_invalid";

    public const string DistributorCompanyInvalid = "wholesale.distributor.company_invalid";

    public const string InvitationSelfLink = "wholesale.invitation.self_link";

    // 422 de guarda: los invariantes de la lista. El validador de Application los atrapa antes con el campo
    // (validation.failed), así que no forman parte del contrato; existen para que el agregado no confíe en quien lo llama
    // (mismo patrón que suppliers.supplier.name_required).
    public const string PriceListNameRequired = "wholesale.price_list.name_required";

    public const string PriceListNameTooLong = "wholesale.price_list.name_too_long";

    public const string PriceListDiscountInvalid = "wholesale.price_list.discount_invalid";

    public const string PriceListVisibilityInvalid = "wholesale.price_list.visibility_invalid";

    public const string PriceListCategoriesRequired = "wholesale.price_list.categories_required";

    public const string PriceListCategoriesTooMany = "wholesale.price_list.categories_too_many";
}
