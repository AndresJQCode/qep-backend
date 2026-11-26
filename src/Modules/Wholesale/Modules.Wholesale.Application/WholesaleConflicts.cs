using BuildingBlocks.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Las respuestas de «no se puede» de los handlers, por tipo y no por status (el mapeo vive en <c>ApiExceptionHandler</c>).
/// El dominio repite las guardas de estado como <see cref="WholesaleDomainException"/> (422) como última red; el status del
/// contrato (409 <c>invalid_transition</c> y <c>already_accepted</c>, 410 <c>expired</c>) sale de acá, antes de mutar
/// (precedente <c>PurchasesConflicts</c>).
/// </summary>
internal static class WholesaleConflicts
{
    // Las búsquedas van acotadas al tenant del llamador: «no encontrada» es «no está entre las tuyas».
    public static ResourceNotFoundException PriceListNotFound(Guid id) =>
        new(WholesaleErrorCodes.PriceListNotFound, $"Price list '{id}' was not found.");

    public static ResourceNotFoundException DistributorNotFound(Guid id) =>
        new(WholesaleErrorCodes.DistributorNotFound, $"Distributor '{id}' was not found.");

    /// <summary>Token desconocido, rotado por un reenvío o distribuidor borrado: los tres son el mismo 404, y el mensaje no
    /// repite el token.</summary>
    public static ResourceNotFoundException InvitationNotFound() =>
        new(WholesaleErrorCodes.InvitationNotFound, "The invitation was not found.");

    public static RequestConcurrencyException Stale() =>
        new("concurrency.conflict", "The resource changed after it was loaded.");

    public static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
        {
            throw Stale();
        }
    }




    public static ResourceConflictException InvalidTransition() =>
        new(WholesaleErrorCodes.DistributorInvalidTransition, "The distributor status does not allow this action.");

    /// <summary>D-14: el 409 sale de acá, antes de llamar al dominio, que repite la guarda como 422.</summary>
    public static void EnsureAllowed(Distributor distributor, DistributorAction action)
    {
        ArgumentNullException.ThrowIfNull(distributor);
        if (!distributor.AllowedActions().Contains(action))
        {
            throw InvalidTransition();
        }
    }

    public static ResourceConflictException IdentificationTaken() =>
        new(WholesaleErrorCodes.DistributorIdentificationTaken, "Another distributor already has that identification number.");

    /// <summary>La aceptación ya empezó (la marca, o el tenant reservado ya creado) con la invitación pendiente: cambiar el
    /// documento duplicaría lo que ese intento creó.</summary>
    public static ResourceConflictException AcceptanceInProgress() =>
        new(WholesaleErrorCodes.DistributorAcceptanceInProgress,
            "El distribuidor ya empezó a aceptar la invitación: no puedes cambiar el tipo ni el número de documento.");

    public static WholesaleDomainException AdvisorInvalid() =>
        new(WholesaleErrorCodes.DistributorAdvisorInvalid, "El asesor no es un miembro activo de la organización.");

    public static WholesaleDomainException CompanyInvalid() =>
        new(WholesaleErrorCodes.DistributorCompanyInvalid,
            "La empresa no existe, está inactiva o su documento no sirve para el proveedor del distribuidor.");

    /// <summary>La ciudad que el mayorista eligió para un distribuidor colombiano no existe en Geography.</summary>
    public static WholesaleDomainException CityNotFound() =>
        new(WholesaleErrorCodes.DistributorCityNotFound, "La ciudad no existe.");

    /// <summary>Spec 2026-11-18: con teléfono, varias conexiones listas para el aviso y ninguna elegida.</summary>
    public static WholesaleDomainException WhatsAppConnectionRequired() =>
        new(WholesaleErrorCodes.DistributorWhatsAppConnectionRequired,
            "Elige desde qué número de WhatsApp se manda el aviso.");

    /// <summary>Spec 2026-11-18: ninguna conexión lista para el aviso, o la elegida no lo está.</summary>
    public static WholesaleDomainException WhatsAppUnavailable() =>
        new(WholesaleErrorCodes.DistributorWhatsAppUnavailable,
            "No hay un número de WhatsApp con la plantilla del aviso aprobada para mandarlo.");

    public static ResourceConflictException InvitationAlreadyAccepted() =>
        new(WholesaleErrorCodes.InvitationAlreadyAccepted, "The invitation was already accepted.");

    public static ResourceGoneException InvitationExpired() =>
        new(WholesaleErrorCodes.InvitationExpired, "The invitation expired.");

    public static ResourceConflictException InvitationUnavailable() =>
        new(WholesaleErrorCodes.InvitationUnavailable, "The wholesaler cannot take distributors right now.");

    /// <summary>Rulings W16 y W18: el mayorista bloqueó la relación; volver a aceptar no la reactiva ni es un doble clic. 403
    /// en todos lados, como dice el spec.</summary>
    public static RequestForbiddenException RelationshipInactive() =>
        new(WholesaleErrorCodes.RelationshipInactive, "The wholesaler blocked this relationship.");

    public static RequestForbiddenException NotTenantOwner() =>
        new(WholesaleErrorCodes.InvitationNotTenantOwner, "Only an owner or admin of the organization can link it.");

    public static WholesaleDomainException SelfLink() =>
        new(WholesaleErrorCodes.InvitationSelfLink, "No puedes vincular la organización del mayorista como su distribuidor.");

    public static ResourceConflictException AlreadyLinked() =>
        new(WholesaleErrorCodes.DistributorAlreadyLinked, "That organization is already a distributor of this wholesaler.");
}
