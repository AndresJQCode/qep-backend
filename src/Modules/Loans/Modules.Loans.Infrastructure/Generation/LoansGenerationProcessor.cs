using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Modules.Loans.Application;
using Modules.Loans.Domain;
using Modules.Loans.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Loans.Infrastructure.Generation;

/// <summary>
/// La corrida diaria de un tenant: cada período que ya cerró se convierte en cuota. Un préstamo que falla se registra y no frena
/// a los demás; el tracker se limpia después de cada uno para que una entidad que no se pudo guardar no vuelva a intentarse en
/// el <c>SaveChanges</c> del siguiente. Un préstamo que falló se retoma en la corrida de mañana, que genera sus períodos con su
/// fecha real. No manda nada, así que no espera una hora de envío: el corte vale desde las 00:00 locales.
/// </summary>
internal sealed partial class LoansGenerationProcessor(
    LoansDbContext dbContext,
    ILoanRepository loans,
    ILoanInstallmentRepository installments,
    ILoanCapitalPaymentRepository payments,
    ITenantClock tenantClock,
    ITenantDirectory tenantDirectory,
    ITenantModules tenantModules,
    TenantRunStore runs,
    ILogger<LoansGenerationProcessor> logger) : ILoansGenerationProcessor
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Loan installment generation failed for loan {LoanId} of tenant {TenantId}.")]
    private static partial void LogGenerationFailed(ILogger logger, Exception exception, Guid loanId, Guid tenantId);

    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);

        // Un tenant suspendido (o en cualquier estado que no sea Active) no recibe servicio. null es el tenant del stub, sin fila.
        if (await tenantDirectory.GetStatusAsync(new TenantId(tenantId), cancellationToken) != TenantStatus.Active)
        {
            return;
        }

        // El módulo puede apagarse con préstamos ya creados: sin él no se genera nada, y como el día no se reclama, la corrida
        // siguiente a prenderlo recupera todos los períodos. null es el tenant del stub.
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is null || !modules.IsEnabled(TenantModuleKeys.Loans))
        {
            return;
        }

        var today = calendar.Today;
        var now = calendar.UtcNow;
        if (!await runs.TryClaimAsync(tenantId, today, now, cancellationToken))
        {
            return;
        }

        // Los ids primero: cada préstamo se relee con tracking dentro de su try, porque el finally limpia el tracker.
        var loanIds = (await loans.ListAccruingAsync(tenantId, cancellationToken)).Select(loan => loan.Id).ToArray();
        dbContext.ChangeTracker.Clear();

        foreach (var loanId in loanIds)
        {
            try
            {
                await GenerateLoanAsync(tenantId, loanId, today, now, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogGenerationFailed(logger, exception, loanId.Value, tenantId);
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        // La hora de cierre, no la de inicio: la corrida puede llevar minutos.
        var finishedAt = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        await runs.TryFinishAsync(tenantId, today, finishedAt, cancellationToken);
    }

    /// <summary>
    /// <c>IX_installments_loan_period</c> es la deduplicación de la generación: chocar es «otra réplica la creó», no un error. Se
    /// relee el préstamo —su <c>last_generated_period_end</c> ya avanzó— y se intenta de nuevo, una vez; lo mismo si alguien
    /// abonó o editó el préstamo en el medio (token de concurrencia).
    /// </summary>
    private async Task GenerateLoanAsync(
        Guid tenantId, LoanId loanId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var loan = await loans.FindAsync(tenantId, loanId, cancellationToken);
            if (loan is null)
            {
                return;
            }

            var loanPayments = await payments.ListByLoanAsync(tenantId, loanId, cancellationToken);
            var generated = LoanInstallmentGenerator.GenerateClosedPeriods(loan, loanPayments, today, now);
            if (generated.Count == 0)
            {
                return;
            }

            installments.AddRange(generated);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception) when (attempt == 0
                && (exception is DbUpdateConcurrencyException || UniqueViolation.Is(exception, "IX_installments_loan_period")))
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }
}
