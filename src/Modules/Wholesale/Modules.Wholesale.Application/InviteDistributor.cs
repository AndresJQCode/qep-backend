using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record InviteDistributorCommand(
    Guid TenantId,
    string Email,
    string BusinessName,
    string IdentificationNumber,
    string ContactName,
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId,
    string CorrelationId)
    : ICommand<DistributorDetailDto>;

/// <summary>
/// Invita a un distribuidor. Orden: 403 → 422 de validación → 404 lista → 422 asesor → 422 empresa → 409 documento. El token
/// plano se genera acá y sólo viaja en el evento hacia el correo; en la base queda su SHA-256 (D-1). El id del tenant que
/// nacerá si acepta creando uno se reserva ya (D-17).
/// </summary>
public sealed class InviteDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IPriceListRepository priceLists,
    IDistributorRepository distributors,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    IWholesaleEventPublisher eventPublisher,
    DistributorComposer composer,
    IValidator<InviteDistributorCommand> validator)
    : ICommandHandler<InviteDistributorCommand, DistributorDetailDto>
{
    // Lo que la auditoría nombra: nunca el token ni su hash.
    private static readonly string[] WrittenFields =
        ["email", "businessName", "identificationNumber", "contactName", "priceListId", "advisorId", "billingCompanyId"];

    public async Task<DistributorDetailDto> HandleAsync(InviteDistributorCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await DistributorTerms.EnsureValidAsync(
            priceLists, members, companies, command.TenantId, command.PriceListId, command.AdvisorId, command.BillingCompanyId,
            cancellationToken);
        if (await distributors.IdentificationExistsAsync(command.TenantId, command.IdentificationNumber.Trim(), cancellationToken))
        {
            throw WholesaleConflicts.IdentificationTaken();
        }

        var token = InvitationTokens.Generate();
        var now = clock.UtcNow;
        var distributor = Distributor.Invite(
            command.TenantId,
            command.Email,
            command.BusinessName,
            command.IdentificationNumber,
            command.ContactName,
            new PriceListId(command.PriceListId),
            command.AdvisorId,
            command.BillingCompanyId,
            executionContext.SubjectId,
            InvitationTokens.HashOf(token),
            Guid.CreateVersion7(),
            now);
        distributors.Add(distributor);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorInvited,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            WrittenFields,
            now);
        eventPublisher.DistributorInvited(
            command.TenantId,
            distributor.Id.Value,
            distributor.Email,
            distributor.BusinessName,
            token,
            distributor.Invitation.ExpiresAt,
            command.CorrelationId,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(distributor, cancellationToken);
    }
}

/// <summary>Las tres verificaciones que repiten invitar y editar.</summary>
internal static class DistributorTerms
{
    public static async Task EnsureValidAsync(
        IPriceListRepository priceLists,
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
