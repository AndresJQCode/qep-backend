using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// El borrador de cotización que pide una integración sin sesión (spec 2026-10-29 §8.3; hoy, la herramienta
/// <c>create_quotation_draft</c> de un agente IA). A diferencia de <see cref="CreateQuotationCommand"/>, la asesora es
/// explícita (P-4: la que el tenant configuró en el agente) y no la persona que llama, porque no hay persona. Las líneas
/// se precian contra el catálogo con las mismas reglas de la creación (<see cref="QuotationDraftBuilder"/>), y el
/// cliente tiene que poder cotizarse igual que en la pantalla: inexistente, incompleto, sin CUC o inactivo lanza el
/// mismo código, en el mismo orden (<c>client_incomplete</c> antes que <c>client_cuc_missing</c>).
/// </summary>
/// <param name="ExternalKey">La clave de idempotencia (D-A16, <c>ai:{jobId}:{callId}</c>): con la misma clave, la
/// segunda llamada devuelve la cotización que ya existe.</param>
public sealed record ExternalQuotationDraft(
    Guid TenantId,
    Guid ClientId,
    Guid AdvisorMembershipId,
    QuotationBillingAccountRequest BillingAccount,
    IReadOnlyList<QuotationItemAddition> Items,
    string? Notes,
    string ExternalKey);

/// <param name="AdvisorMembershipId">La asesora de la cotización guardada: con <see cref="AlreadyExisted"/> es la de la
/// primera llamada, que puede no ser la de este borrador.</param>
public sealed record ExternalQuotationResult(
    Guid QuotationId, string QuotationNumber, Guid AdvisorMembershipId, bool AlreadyExisted = false);

/// <summary>
/// El único camino para crear una cotización sin sesión HTTP. Un módulo consumidor declara su propio puerto y su
/// adaptador en Bootstrapper llama a éste; nunca referencia <c>Modules.Quotations.Application</c>. Siempre nace en
/// <see cref="QuotationStatus.Draft"/>: la revisa y la envía una persona (spec 2026-10-29 D-A30 y P-4b).
/// </summary>
public interface IExternalQuotationWriter
{
    Task<ExternalQuotationResult> CreateDraftAsync(ExternalQuotationDraft draft, CancellationToken cancellationToken);
}

/// <summary>
/// Los mismos pasos de <see cref="CreateQuotationHandler"/> sin sesión: validación del cuerpo, consecutivo y cotización en
/// una sola transacción, historial sin miembro y auditoría por integración. La carrera de la clave la cierra
/// <c>IX_quotations_tenant_external_key</c>, como <see cref="KeyedOrderCommit"/> con los pedidos: quien pierde deshace su
/// transacción (el consecutivo vuelve atrás) y devuelve la cotización que ganó.
/// </summary>
public sealed class ExternalQuotationService(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    QuotationDraftBuilder drafts,
    IQuotationNumberGenerator numberGenerator,
    IDocumentNumberingFormatLookup numberingFormats,
    ITenantDirectory tenantDirectory,
    ITenantClock tenantClock,
    IValidator<CreateQuotationCommand> validator)
    : IExternalQuotationWriter
{
    public async Task<ExternalQuotationResult> CreateDraftAsync(ExternalQuotationDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ExternalKey);
        var externalKey = draft.ExternalKey.Trim();

        // D-A16: un trabajo retomado encuentra su cotización antes de validar nada; si el cliente cambió después, igual la
        // devuelve.
        if (await repository.FindByExternalKeyAsync(draft.TenantId, externalKey, cancellationToken) is { } existing)
        {
            return Existing(existing);
        }

        // El mismo cuerpo que la creación de la pantalla, sin partes ni vigencia: la cotización usa los datos del cliente y
        // la vigencia por defecto.
        var command = new CreateQuotationCommand(
            draft.TenantId, draft.ClientId, ValidUntil: null, PaymentMethod: null, draft.Notes, Parties: null,
            draft.BillingAccount, Items: draft.Items);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var advisor = new MemberId(draft.AdvisorMembershipId);
        var calendar = await tenantClock.GetAsync(draft.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var format = await numberingFormats.GetAsync(draft.TenantId, DocumentNumberType.Quotation, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;

        var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            var sequence = await numberGenerator.NextAsync(draft.TenantId, counterYear, cancellationToken);
            var quotationNumber = DocumentNumberFormatter.Format(format, year, sequence);
            var built = await drafts.BuildAsync(
                new QuotationDraftInput(draft.TenantId, draft.ClientId, quotationNumber, advisor, command, calendar.Today, now),
                cancellationToken);
            var quotation = built.Quotation;
            quotation.AssignExternalKey(externalKey);
            quotation.EnsureBillingIdentified();
            BillingPartyFiscalRules.EnsureConsistent(quotation.Billing?.ToDetails());
            await BillingPersonTypeRequirement.EnsureForQuotationAsync(tenantDirectory, draft.TenantId, quotation, cancellationToken);

            repository.Add(quotation);
            // Sin miembro: lo creó el agente, no la asesora. Las líneas van en la misma fila de la creación; el historial por
            // línea (QuotationItemEditHistory) audita con la persona que edita, y aquí no hay ninguna.
            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Created,
                memberId: null,
                QuotationChangeSummary.CreatedByAgent(built.CustomerName, quotation.Currency),
                now));
            auditPublisher.PublishByIntegration(
                draft.TenantId, "quotation.quotation.created", quotation.Id.ToString(), "success", now);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return new ExternalQuotationResult(quotation.Id.Value, quotation.QuotationNumber, advisor.Value);
        }
        catch (QuotationsDomainException error) when (error.Code == ExternalQuotationErrors.ExternalKeyTaken)
        {
            // Otra llamada con la misma clave guardó primero: se lee a la ganadora después de deshacer la transacción.
        }
        finally
        {
            await transaction.DisposeAsync();
            if (!committed)
            {
                // Lo que quedó a medias (la cotización, su historial, la auditoría) no debe viajar en el próximo guardado del
                // scope.
                unitOfWork.DiscardPendingChanges();
            }
        }

        var winner = await repository.FindByExternalKeyAsync(draft.TenantId, externalKey, cancellationToken)
            ?? throw new InvalidOperationException("The external key was taken, but no committed quotation carries it.");
        return Existing(winner);
    }

    private static ExternalQuotationResult Existing(Quotation quotation) =>
        new(quotation.Id.Value, quotation.QuotationNumber, quotation.AdvisorId.Value, AlreadyExisted: true);
}
