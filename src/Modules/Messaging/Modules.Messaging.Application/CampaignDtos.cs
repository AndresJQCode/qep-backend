namespace Modules.Messaging.Application;

// BFF (CLAUDE.md), spec 2026-10-11 §6.2: copia literal del contrato que el frontend ya implementó. Los enums viajan por nombre
// (Draft, Sending, Pending, Delivered…); los contadores viajan completos incluso en cero, así la pantalla nunca lee `undefined`;
// `failureReason` es el texto en español que el backend calcula —la pantalla lo muestra tal cual— y `failureCode` el código estable
// para filtrar ("opted_out", "131049").

/// <summary>
/// <c>sent</c> son los destinatarios en <c>Sent</c> («Meta lo aceptó»); <c>delivered</c> y <c>read</c> son subconjuntos de <c>sent</c>
/// que salen del estado del mensaje (leído implica entregado) y <c>replied</c> cuenta los <c>Sent</c> con un entrante posterior.
/// <c>pending</c> suma <c>Pending</c> y <c>Sending</c>. <c>total</c> es la suma de <c>pending</c>, <c>sent</c>, <c>failed</c> y
/// <c>skipped</c>, así la barra de progreso cierra. <c>retriable</c> no suma: es cuántos reencolaría <c>POST /{id}/retry</c> ahora
/// (los <c>Failed</c> que no son opt-out más los <c>Skipped campaign_failed</c>), para que la pantalla no lo calcule.
/// </summary>
public sealed record CampaignCountsDto(int Total, int Pending, int Sent, int Delivered, int Read, int Replied, int Failed, int Skipped, int Retriable);

public sealed record CampaignConnectionDto(Guid Id, string Name);

public sealed record CampaignTemplateDto(string Name, string Language, string Category);

public class CampaignSummaryDto(
    Guid id,
    string name,
    string status,
    string? failureReason,
    string? failureCode,
    CampaignConnectionDto connection,
    CampaignTemplateDto template,
    DateTimeOffset? scheduledAt,
    DateTimeOffset? startedAt,
    DateTimeOffset? completedAt,
    MemberRefDto createdBy,
    CampaignCountsDto counts,
    DateTimeOffset createdAt,
    DateTimeOffset updatedAt,
    long version,
    int retryCount)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Status { get; } = status;

    public string? FailureReason { get; } = failureReason;

    public string? FailureCode { get; } = failureCode;

    public CampaignConnectionDto Connection { get; } = connection;

    public CampaignTemplateDto Template { get; } = template;

    public DateTimeOffset? ScheduledAt { get; } = scheduledAt;

    public DateTimeOffset? StartedAt { get; } = startedAt;

    public DateTimeOffset? CompletedAt { get; } = completedAt;

    public MemberRefDto CreatedBy { get; } = createdBy;

    public CampaignCountsDto Counts { get; } = counts;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public DateTimeOffset UpdatedAt { get; } = updatedAt;

    public long Version { get; } = version;

    /// <summary>Cuántas veces se reintentaron sus fallidos.</summary>
    public int RetryCount { get; } = retryCount;
}

/// <summary><c>source</c> es <c>text</c> (con <c>value</c>), <c>customerName</c> o <c>greeting</c> (sin).</summary>
public sealed record CampaignParameterDto(string Source, string? Value);

public sealed record CampaignAudienceTagDto(Guid Id, string Name, string Color);

public sealed record CampaignAudienceCustomerDto(Guid Id, string Name);

/// <summary>Una etiqueta o un cliente borrados no aparecen.</summary>
public sealed record CampaignAudienceDto(IReadOnlyList<CampaignAudienceTagDto> Tags, IReadOnlyList<CampaignAudienceCustomerDto> Customers);

public sealed class CampaignDetailDto(
    CampaignSummaryDto summary,
    IReadOnlyDictionary<string, CampaignParameterDto> parameters,
    CampaignAudienceDto audience)
    : CampaignSummaryDto(
        summary?.Id ?? throw new ArgumentNullException(nameof(summary)),
        summary.Name,
        summary.Status,
        summary.FailureReason,
        summary.FailureCode,
        summary.Connection,
        summary.Template,
        summary.ScheduledAt,
        summary.StartedAt,
        summary.CompletedAt,
        summary.CreatedBy,
        summary.Counts,
        summary.CreatedAt,
        summary.UpdatedAt,
        summary.Version,
        summary.RetryCount)
{
    public IReadOnlyDictionary<string, CampaignParameterDto> Parameters { get; } = parameters;

    public CampaignAudienceDto Audience { get; } = audience;
}

/// <summary><c>retryCount</c> es en qué reintento de la campaña se reencoló este destinatario (cero: sólo el envío original).
/// <c>retriable</c> dice si un reintento lo volvería a intentar; un <c>Failed</c> por opt-out (131050) viaja con <c>false</c> para que la
/// pantalla lo marque. <c>replied</c>: hay un entrante suyo después del envío. <c>readWithoutReceipt</c>: respondió pero Meta nunca avisó
/// la lectura —casi siempre porque apagó las confirmaciones de lectura—; la pantalla lo muestra como leído y lo marca.</summary>
public sealed record CampaignRecipientDto(
    Guid CustomerId,
    string CustomerName,
    string Status,
    string? FailureReason,
    string? FailureCode,
    DateTimeOffset? SentAt,
    Guid? ConversationId,
    string? MessageStatus,
    int RetryCount,
    bool Retriable,
    bool Replied,
    bool ReadWithoutReceipt);

public sealed record CampaignPageDto<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary><c>reachable</c> es a cuántos les llegaría hoy: el total menos inactivos, sin WhatsApp y —sólo si la plantilla es
/// <c>MARKETING</c>— quienes apagaron las promociones. <c>optedOut</c> viaja siempre, se reste o no.</summary>
public sealed record AudiencePreviewDto(int Total, int Reachable, int OptedOut, int Inactive, int WithoutWhatsApp);
