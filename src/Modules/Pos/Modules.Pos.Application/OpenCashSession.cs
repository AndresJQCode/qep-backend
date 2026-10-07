using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

public sealed record OpenCashSessionCommand(Guid TenantId, Guid? CompanyId, decimal OpeningFloat)
    : ICommand<PosOpenSessionResponse>;

// El validador da el campo (422 validation.failed con errors); el dominio da el código. Los dos
// existen aunque se repitan, como pide la convención.
public sealed class OpenCashSessionValidator : AbstractValidator<OpenCashSessionCommand>
{
    public OpenCashSessionValidator()
    {
        RuleFor(command => command.OpeningFloat)
            .InclusiveBetween(0m, PosLimits.MaxCashAmount)
            .Must(PosLimits.HasValidScale).WithMessage("The opening float accepts at most 2 decimals.");
    }
}

public sealed class OpenCashSessionHandler(
    ICashSessionRepository sessions,
    IPosCompanyLookup companies,
    IPosCashierLookup cashiers,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<OpenCashSessionCommand> validator)
    : ICommandHandler<OpenCashSessionCommand, PosOpenSessionResponse>
{
    public async Task<PosOpenSessionResponse> HandleAsync(
        OpenCashSessionCommand command, CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: un llamador ajeno no se lleva el mapa de errores.
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.RegisterOperate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Por legibilidad; dos aperturas simultáneas sólo las frena el índice parcial.
        if (await sessions.FindOpenByCashierAsync(command.TenantId, cashier, cancellationToken) is not null)
        {
            throw new PosDomainException("pos.session.already_open", "The cashier already has an open cash session.");
        }

        var company = await ResolveCompanyAsync(command, cancellationToken);
        var name = await cashiers.FindNameAsync(command.TenantId, cashier.Value, cancellationToken) ?? cashier.Value.ToString();
        var now = clock.UtcNow;
        var session = CashSession.Open(
            CashSessionId.New(),
            command.TenantId,
            cashier,
            name,
            new PosCompanySnapshot(company.Id, company.Name, company.TaxId, company.Address, company.Phone),
            command.OpeningFloat,
            now);

        sessions.Add(session);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "pos.session.opened", "cash_session",
            session.Id.ToString(), "success", [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        return PosSessionMapping.ToOpenResponse(session, calendar);
    }

    private async Task<PosCompanyRef> ResolveCompanyAsync(OpenCashSessionCommand command, CancellationToken cancellationToken)
    {
        if (command.CompanyId is { } companyId)
        {
            // Mismo código para "no existe" y "es de otro tenant".
            var chosen = await companies.FindAsync(command.TenantId, companyId, cancellationToken)
                ?? throw new PosDomainException("pos.session.company_not_found", "The company was not found.");
            return chosen.IsActive
                ? chosen
                : throw new PosDomainException("pos.session.company_inactive", "The company is inactive.");
        }

        var active = await companies.ListActiveAsync(command.TenantId, cancellationToken);
        return active.Count switch
        {
            0 => throw new PosDomainException("pos.session.no_active_company", "The tenant has no active company."),
            1 => active[0],
            _ => throw new PosDomainException("pos.session.company_required", "Choose the issuing company."),
        };
    }
}
