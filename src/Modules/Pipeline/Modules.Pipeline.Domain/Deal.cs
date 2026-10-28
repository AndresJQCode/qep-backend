namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.2, D13–D19: un negocio. Cerrado si y sólo si <see cref="ClosedAt"/> no es nulo (P3 del plan); el tipo de
/// la etapa sigue siendo lo que el dominio valida. Cada cambio de etapa deja una fila en el historial pendiente
/// (<see cref="PullHistory"/>), y la unidad de trabajo la guarda en el mismo SaveChanges que el negocio (P4): ningún camino puede
/// mover la etapa sin su fila. <see cref="Version"/> es el token de concurrencia.
/// </summary>
public sealed class Deal
{
    public const int TitleMaxLength = 200;
    public const int LossNoteMaxLength = 500;

    /// <summary>El mayor importe que cabe en <c>numeric(14,2)</c>, igual que <c>PosLimits.MaxAmount</c>.</summary>
    public const decimal MaxAmount = 999_999_999_999.99m;

    private readonly List<DealStageHistoryEntry> _pendingHistory = [];

    private Deal()
    {
        Title = string.Empty;
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid PipelineId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid OwnerMemberId { get; private set; }

    public string Title { get; private set; }

    /// <summary>D34: nulo = sin monto todavía. Cuenta en el tablero, no suma.</summary>
    public decimal? Amount { get; private set; }

    public string Currency { get; private set; }

    public DateOnly? ExpectedCloseDate { get; private set; }

    public Guid StageId { get; private set; }

    public DateTimeOffset StageEnteredAt { get; private set; }

    public Guid? PrimaryQuotationId { get; private set; }

    public DealSource Source { get; private set; }

    public Guid? OriginConversationId { get; private set; }

    public Guid? LossReasonId { get; private set; }

    public string? LossNote { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>§5.2: el último cambio del negocio, etapa incluida. Entra en el cálculo de estancado.</summary>
    public DateTimeOffset LastChangedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>P4 del plan: cuántas filas de historial lleva el negocio. La siguiente fila toma <c>HistoryCount + 1</c> como
    /// <see cref="DealStageHistoryEntry.Ordinal"/>. Viaja con el negocio y lo protege su <see cref="Version"/>.</summary>
    public int HistoryCount { get; private set; }

    public bool IsClosed => ClosedAt is not null;

    public static Deal Create(
        Guid tenantId, SalesPipeline pipeline, Guid customerId, Guid ownerMemberId, string? title, decimal? amount, string currency,
        DateOnly? expectedCloseDate, DealSource source, Guid? originConversationId, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var stage = pipeline.FirstOpenStage;
        var deal = new Deal
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PipelineId = pipeline.Id,
            CustomerId = customerId,
            OwnerMemberId = ownerMemberId,
            Title = NormalizeTitle(title),
            Amount = ValidateAmount(amount),
            Currency = currency,
            ExpectedCloseDate = expectedCloseDate,
            StageId = stage.Id,
            StageEnteredAt = now,
            Source = source,
            OriginConversationId = source == DealSource.Conversation ? originConversationId : null,
            LastChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
        deal.Record(null, stage, actor, now);
        return deal;
    }

    /// <summary>P5 del plan: reemplaza los campos editables. Con principal (D18), monto y moneda no pueden cambiar.</summary>
    public void Update(string? title, Guid ownerMemberId, decimal? amount, string currency, DateOnly? expectedCloseDate, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var normalizedTitle = NormalizeTitle(title);
        var validAmount = ValidateAmount(amount);
        if (PrimaryQuotationId is not null && (validAmount != Amount || !string.Equals(currency, Currency, StringComparison.Ordinal)))
        {
            throw new PipelineDomainException(
                PipelineErrorCodes.DealAmountFollowsQuotation, "The amount follows the primary quotation; change the quotation instead.");
        }

        Title = normalizedTitle;
        OwnerMemberId = ownerMemberId;
        Amount = validAmount;
        Currency = currency;
        ExpectedCloseDate = expectedCloseDate;
        Touch(now);
    }

    /// <summary>D13: libre entre abiertas. A una de cierre no: para eso están <see cref="Win"/> y <see cref="Lose"/>.</summary>
    public bool MoveTo(SalesPipeline pipeline, Guid stageId, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOpen();
        var target = pipeline.FindStage(stageId)
            ?? throw new PipelineDomainException(PipelineErrorCodes.StageNotFound, "The stage does not belong to this pipeline.");
        if (target.Kind != StageKind.Open)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealStageNotOpen, "Use win or lose to close a deal.");
        }

        if (target.Id == StageId)
        {
            return false;
        }

        ChangeStage(target, actor, now);
        return true;
    }

    /// <summary>D17: a la etapa de cotización sólo si está antes; nunca hacia atrás ni sobre un cerrado.</summary>
    public bool AdvanceToQuotationStage(SalesPipeline pipeline, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (IsClosed)
        {
            return false;
        }

        var target = pipeline.QuotationStage;
        if (pipeline.FindStage(StageId) is { } current && current.Position >= target.Position)
        {
            return false;
        }

        ChangeStage(target, actor, now);
        return true;
    }

    public void Win(SalesPipeline pipeline, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOpen();
        ChangeStage(pipeline.WonStage, actor, now);
        ClosedAt = now;
    }

    /// <summary>D14 y P10: el motivo es obligatorio (que sea del catálogo activo lo revisa el handler); la nota, opcional.</summary>
    public void Lose(SalesPipeline pipeline, Guid? lossReasonId, string? note, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        EnsureOpen();
        if (lossReasonId is not { } reasonId || reasonId == Guid.Empty)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealLossReasonRequired, "Losing a deal needs a reason from the catalog.");
        }

        var normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalizedNote is { Length: > LossNoteMaxLength })
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealLossNoteInvalid, $"The note takes at most {LossNoteMaxLength} characters.");
        }

        ChangeStage(pipeline.LostStage, actor, now);
        ClosedAt = now;
        LossReasonId = reasonId;
        LossNote = normalizedNote;
    }

    /// <summary>D15: a la última etapa abierta del historial (<paramref name="lastOpenStageId"/>, la que el repositorio lee de la
    /// fila de cierre); si ya no existe o no es abierta, a la primera abierta.</summary>
    public void Reopen(SalesPipeline pipeline, Guid? lastOpenStageId, DealActor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (!IsClosed)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealNotClosed, "Only a won or lost deal can be reopened.");
        }

        var target = lastOpenStageId is { } id && pipeline.FindStage(id) is { Kind: StageKind.Open } last
            ? last
            : pipeline.FirstOpenStage;
        ChangeStage(target, actor, now);
        ClosedAt = null;
        LossReasonId = null;
        LossNote = null;
    }

    /// <summary>D17 y D18: la cotización pasa a ser la principal y el monto la sigue.</summary>
    public void SetPrimaryQuotation(Guid quotationId, decimal total, string currency, DateTimeOffset now)
    {
        PrimaryQuotationId = quotationId;
        Amount = total;
        Currency = currency;
        Touch(now);
    }

    /// <summary>D18 y D26: el monto se refresca con el reenvío y la conversión de la principal. Sin cambio, nada.</summary>
    public void FollowPrimary(decimal total, string currency, DateTimeOffset now)
    {
        if (Amount == total && string.Equals(Currency, currency, StringComparison.Ordinal))
        {
            return;
        }

        Amount = total;
        Currency = currency;
        Touch(now);
    }

    /// <summary>D27: sin principal, el monto queda manual con su último valor.</summary>
    public void ClearPrimary(DateTimeOffset now)
    {
        if (PrimaryQuotationId is null)
        {
            return;
        }

        PrimaryQuotationId = null;
        Touch(now);
    }

    /// <summary>P4 del plan: las filas que la unidad de trabajo guarda en el mismo SaveChanges.</summary>
    public IReadOnlyList<DealStageHistoryEntry> PullHistory()
    {
        var pending = _pendingHistory.ToArray();
        _pendingHistory.Clear();
        return pending;
    }

    private void ChangeStage(PipelineStage target, DealActor actor, DateTimeOffset now)
    {
        var from = StageId;
        StageId = target.Id;
        StageEnteredAt = now;
        Touch(now);
        Record(from, target, actor, now);
    }

    private void Record(Guid? fromStageId, PipelineStage to, DealActor actor, DateTimeOffset now)
    {
        HistoryCount++;
        _pendingHistory.Add(DealStageHistoryEntry.Create(
            TenantId, Id, PipelineId, HistoryCount, fromStageId, to.Id, now, actor, Amount, Currency, to.Probability));
    }

    private void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new PipelineDomainException(PipelineErrorCodes.DealAlreadyClosed, "The deal is already closed; reopen it first.");
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
        LastChangedAt = now;
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty).Trim();
        return normalized.Length is > 0 and <= TitleMaxLength
            ? normalized
            : throw new PipelineDomainException(PipelineErrorCodes.DealTitleInvalid, $"The title takes 1 to {TitleMaxLength} characters.");
    }

    private static decimal? ValidateAmount(decimal? amount) =>
        amount is not { } value || (value >= 0 && value <= MaxAmount && value == Math.Round(value, 2))
            ? amount
            : throw new PipelineDomainException(
                PipelineErrorCodes.DealAmountInvalid, "The amount goes from 0 to 999999999999.99 with at most 2 decimals.");
}
