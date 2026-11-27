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


    public const string DistributorIdentificationTaken = "wholesale.distributor.identification_taken";

    public const string DistributorIdentificationNumberNotNumeric =
        "wholesale.distributor.identification_number_not_numeric";

    public const string DistributorInvalidTransition = "wholesale.distributor.invalid_transition";

    public const string DistributorAlreadyLinked = "wholesale.distributor.already_linked";

    // El PUT cambia el documento y el tenant reservado de la invitación pendiente ya existe (aceptación a medias).
    public const string DistributorAcceptanceInProgress = "wholesale.distributor.acceptance_in_progress";

    public const string InvitationAlreadyAccepted = "wholesale.invitation.already_accepted";

    public const string InvitationUnavailable = "wholesale.invitation.unavailable";

    // 410
    public const string InvitationExpired = "wholesale.invitation.expired";

    // 422

    public const string DistributorAdvisorInvalid = "wholesale.distributor.advisor_invalid";

    public const string DistributorCompanyInvalid = "wholesale.distributor.company_invalid";
    public const string DistributorCityNotFound = "wholesale.distributor.city_not_found";

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






}
