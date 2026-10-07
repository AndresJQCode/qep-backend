using BuildingBlocks.Application;
using FluentValidation;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <param name="ExpectedVersion">La version de GET /pos/register que pintó el arqueo; llega por If-Match.</param>
public sealed record CloseCashSessionCommand(
    Guid TenantId, Guid SessionId, long ExpectedVersion, decimal CountedCash, string? Note)
    : ICommand<PosSessionSummaryResponse>;

public sealed class CloseCashSessionValidator : AbstractValidator<CloseCashSessionCommand>
{
    public CloseCashSessionValidator()
    {
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
        RuleFor(command => command.CountedCash)
            .InclusiveBetween(0m, PosLimits.MaxCountedCash)
            .Must(PosLimits.HasValidScale).WithMessage("The counted cash accepts at most 2 decimals.");
        RuleFor(command => command.Note).MaximumLength(PosLimits.NoteMaxLength);
    }
}

public sealed class CloseCashSessionHandler(
    ICashSessionRepository sessions,
    IPosUnitOfWork unitOfWork,
    IPosAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantClock tenantClock,
    IValidator<CloseCashSessionCommand> validator)
    : ICommandHandler<CloseCashSessionCommand, PosSessionSummaryResponse>
{
    public async Task<PosSessionSummaryResponse> HandleAsync(
        CloseCashSessionCommand command, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, command.TenantId, PosPermissions.RegisterOperate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var session = await sessions.FindAsync(command.TenantId, new CashSessionId(command.SessionId), cancellationToken)
            ?? throw PosNotFound.Session(command.SessionId);
        if (session.CashierId != cashier)
        {
            // Sólo el cajero dueño cierra (spec, decisión 5; el cierre forzado es DECISIÓN-PENDIENTE 7).
            throw PosAuthorization.Denied();
        }

        // El cajero cierra contra el arqueo que vio: si entró una venta o una anulación después, el
        // cierre no la absorbe sin que la pantalla la muestre.
        if (session.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict", "The cash session changed after the count was loaded.");
        }

        var now = clock.UtcNow;
        session.Close(command.CountedCash, command.Note, now);
        auditPublisher.Publish(
            command.TenantId, executionContext.SubjectId, "pos.session.closed", "cash_session",
            session.Id.ToString(), "success", [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return PosSessionMapping.ToSummary(session, await tenantClock.GetAsync(command.TenantId, cancellationToken));
    }
}
