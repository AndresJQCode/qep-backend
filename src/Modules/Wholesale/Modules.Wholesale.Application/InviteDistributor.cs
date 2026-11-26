using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record InviteDistributorCommand(
    Guid TenantId,
    string Email,
    string BusinessName,
    string Country,
    string IdentificationType,
    string IdentificationNumber,
    string ContactName,
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId,
    string CorrelationId,
    string? Phone = null,
    Guid? WhatsAppConnectionId = null,
    Guid? CityId = null,
    string? CityName = null,
    string? Address = null)
    : ICommand<DistributorDetailDto>, IDistributorProfile;

/// <summary>
/// Invita a un distribuidor. Orden: 403 → 422 de validación → 404 lista → 422 asesor → 422 empresa → 422 ciudad → 409 documento → 422
/// <c>Phone</c> → 422 conexión. El token plano se genera acá y sólo viaja en el evento hacia el correo; en la base queda su
/// SHA-256 (D-1). Igual la prueba de buzón (spec 2026-11-24), que además no va en la respuesta ni en el aviso por WhatsApp.
/// El id del tenant que nacerá si acepta creando uno se reserva ya (D-17). Con teléfono (spec 2026-11-18) sale
/// además, en el mismo commit, el evento del aviso por WhatsApp (<c>wholesale.distributor-invitation-link.v1</c>, spec
/// 2026-11-19 §2), que lleva el token sólo en el payload del outbox; el mensaje de WhatsApp todavía sale sin el link. Sin
/// teléfono la conexión se ignora. La respuesta
/// lleva el link de la invitación para «Copiar» (spec 2026-11-19 §1).
/// </summary>
public sealed class InviteDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IWholesalePriceLists priceLists,
    IDistributorRepository distributors,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    IWholesaleCityLookup cities,
    ITenantDirectory tenants,
    IWholesalePhoneNormalizer phones,
    IWholesaleWhatsAppConnections whatsAppConnections,
    IMembershipDirectory memberships,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IWholesaleEventPublisher eventPublisher,
    IWholesaleInvitationLinks links,
    DistributorComposer composer,
    IValidator<InviteDistributorCommand> validator)
    : ICommandHandler<InviteDistributorCommand, DistributorDetailDto>
{
    // Lo que la auditoría nombra: nunca el token ni su hash, ni el teléfono (sólo si hubo aviso, D-8).
    private static readonly string[] WrittenFields =
        ["email", "businessName", "country", "identificationType", "identificationNumber", "contactName", "priceListId", "advisorId", "billingCompanyId", "cityId", "cityName", "address"];

    public async Task<DistributorDetailDto> HandleAsync(InviteDistributorCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await DistributorTerms.EnsureValidAsync(
            priceLists, members, companies, command.TenantId, command.PriceListId, command.AdvisorId, command.BillingCompanyId,
            cancellationToken);

        await DistributorTerms.EnsureCityExistsAsync(cities, command.Country, command.CityId, cancellationToken);

        if (await distributors.IdentificationExistsAsync(
                command.TenantId, command.IdentificationType.Trim().ToUpperInvariant(), command.IdentificationNumber.Trim(),
                excluding: null, cancellationToken))
        {
            throw WholesaleConflicts.IdentificationTaken();
        }

        var phone = await DistributorWhatsAppNotice.NormalizeAsync(tenants, phones, command.TenantId, command.Phone, cancellationToken);
        Guid? connectionId = phone is null
            ? null
            : await DistributorWhatsAppNotice.ResolveConnectionAsync(
                whatsAppConnections, command.TenantId, command.WhatsAppConnectionId, cancellationToken);

        var token = InvitationTokens.Generate();
        var (emailProof, emailProofHash) = DistributorEmailProofs.Issue();
        var now = clock.UtcNow;
        var distributor = Distributor.Invite(
            command.TenantId,
            command.Email,
            command.BusinessName,
            command.Country,
            command.IdentificationType,
            command.IdentificationNumber,
            command.ContactName,
            new PriceListId(command.PriceListId),
            command.AdvisorId,
            command.BillingCompanyId,
            executionContext.SubjectId,
            InvitationTokens.HashOf(token),
            Guid.CreateVersion7(),
            phone,
            connectionId,
            now,
            command.CityId,
            command.CityName,
            command.Address,
            emailProofHash);
        distributors.Add(distributor);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorInvited,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            [.. WrittenFields, DistributorWhatsAppNotice.AuditFlag(phone is not null)],
            now);
        eventPublisher.DistributorInvited(
            command.TenantId,
            distributor.Id.Value,
            distributor.Email,
            distributor.BusinessName,
            token,
            emailProof,
            distributor.Invitation.ExpiresAt,
            command.CorrelationId,
            now);
        if (phone is not null)
        {
            await DistributorWhatsAppNotice.QueueAsync(
                eventPublisher, memberships, executionContext.SubjectId, distributor, token, command.CorrelationId, now,
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Spec 2026-11-19 §1: el link para «Copiar», con el mismo token que va al correo; el primer «Copiar» no rota nada.
        var detail = await composer.DetailAsync(distributor, cancellationToken);
        return detail with { InvitationUrl = links.Compose(token) };
    }
}

/// <summary>Las verificaciones contra otros datos que repiten invitar y editar.</summary>
internal static class DistributorTerms
{
    /// <summary>
    /// Sólo en Colombia la ciudad es una fila de Geography. Sin esta verificación un <c>CityId</c> inexistente quedaría en la
    /// invitación y después haría fallar cada aceptación con <c>companies.company.city_not_found</c>. Sin <c>CityId</c> (sólo
    /// una invitación vieja que se edita sin tocar la ciudad) no hay qué buscar.
    /// </summary>
    public static async Task EnsureCityExistsAsync(
        IWholesaleCityLookup cities, string country, Guid? cityId, CancellationToken cancellationToken)
    {
        if (DistributorProfileValidator.IsColombian(country)
            && cityId is { } id
            && await cities.FindAsync(id, cancellationToken) is null)
        {
            throw WholesaleConflicts.CityNotFound();
        }
    }

    public static async Task EnsureValidAsync(
        IWholesalePriceLists priceLists,
        IWholesaleMemberDirectory members,
        IWholesaleCompanyLookup companies,
        Guid tenantId,
        Guid priceListId,
        Guid advisorId,
        Guid billingCompanyId,
        CancellationToken cancellationToken)
    {
        if (await priceLists.GetAsync(tenantId, new PriceListId(priceListId), cancellationToken) is null)
        {
            throw WholesaleConflicts.PriceListNotFound(priceListId);
        }

        // D-15: una membresía activa del tenant, sin filtro de rol.
        var active = await members.ListActiveAsync(tenantId, cancellationToken);
        if (active.All(member => member.MemberId != advisorId))
        {
            throw WholesaleConflicts.AdvisorInvalid();
        }

        // D-2: la empresa da documento y ciudad al proveedor vinculado; tiene que estar activa y con un documento que
        // Suppliers admita.
        var company = await companies.FindAsync(tenantId, billingCompanyId, cancellationToken);
        if (company is not { IsActive: true, Eligible: true })
        {
            throw WholesaleConflicts.CompanyInvalid();
        }
    }
}
