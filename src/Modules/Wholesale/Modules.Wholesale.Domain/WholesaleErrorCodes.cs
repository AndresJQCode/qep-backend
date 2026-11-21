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

    // 403. Spec 2026-11-19: wholesale.invitation.email_mismatch se retiró; el link no exige el correo invitado.
    public const string InvitationNotTenantOwner = "wholesale.invitation.not_tenant_owner";

    // Spec 2026-11-17 y ruling W18: 403 en todos lados; en esta fase, al aceptar una relación bloqueada.
    public const string RelationshipInactive = "wholesale.relationship.inactive";

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

    // Spec 2026-11-18 (aviso por WhatsApp): con teléfono y varias conexiones listas sin elegir una; y sin ninguna lista, o
    // con una elegida que no lo está.
    public const string DistributorWhatsAppConnectionRequired = "wholesale.distributor.whatsapp_connection_required";

    public const string DistributorWhatsAppUnavailable = "wholesale.distributor.whatsapp_unavailable";

    public const string InvitationSelfLink = "wholesale.invitation.self_link";

    // Fase 3: una variante fuera de la visibilidad de la lista, inactiva, ajena o sin precio en la moneda del mayorista.
    public const string ProductNotVisible = "wholesale.product.not_visible";

    // Spec 2026-11-20: un `categoryId` que el comprador no pudo sacar de su árbol visible (ajeno, borrado o fuera de la lista);
    // no dice cuál de los tres.
    public const string CategoryNotVisible = "wholesale.category.not_visible";

    // 422 de guarda: los invariantes de la lista. El validador de Application los atrapa antes con el campo
    // (validation.failed), así que no forman parte del contrato; existen para que el agregado no confíe en quien lo llama
    // (mismo patrón que suppliers.supplier.name_required).
    public const string PriceListNameRequired = "wholesale.price_list.name_required";

    public const string PriceListNameTooLong = "wholesale.price_list.name_too_long";

    public const string PriceListDiscountInvalid = "wholesale.price_list.discount_invalid";

    public const string PriceListVisibilityInvalid = "wholesale.price_list.visibility_invalid";

    public const string PriceListCategoriesRequired = "wholesale.price_list.categories_required";

    public const string PriceListCategoriesTooMany = "wholesale.price_list.categories_too_many";

    /// <summary>Spec 2026-11-20: más exclusiones de las que caben en una lista.</summary>
    public const string PriceListExclusionsTooMany = "wholesale.price_list.exclusions_too_many";
}
