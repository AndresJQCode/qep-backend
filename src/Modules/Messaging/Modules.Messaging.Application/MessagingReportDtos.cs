using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-28: cómo se agrupa la serie del reporte. Llega como texto (<c>day|week|month</c>) para que un valor inválido sea 422 y no 400.</summary>
public enum ReportGrouping
{
    Day,
    Week,
    Month,
}

/// <summary>La ventana de un periodo: días locales del tenant y los instantes UTC que los acotan (fin exclusivo).</summary>
public sealed record ReportWindow(DateOnly From, DateOnly To, DateTimeOffset StartUtc, DateTimeOffset EndExclusiveUtc);

/// <summary>
/// Una sesión (spec 2026-10-28 S1): el primer arranque de una conversación o un <c>Reopened</c>. <c>StartInboundAt</c> es el primer
/// entrante de la sesión (<c>null</c> = sin reloj de respuesta, S3); <c>FirstAnyOutboundAt</c> el primer saliente que cuenta (chatbot
/// incluido); <c>FirstMemberOutboundAt</c> el primero de un miembro con autor. Todo en <c>created_at</c>, nunca <c>occurred_at</c>.
/// <c>FirstInboundEverAt</c> es el primer entrante de toda la conversación.
/// </summary>
public sealed record ReportSessionRow(
    Guid ConversationId,
    Guid ConnectionId,
    ConversationChannel Channel,
    Guid? AssignedMemberId,
    DateTimeOffset SessionStart,
    DateTimeOffset? StartInboundAt,
    DateTimeOffset? FirstAnyOutboundAt,
    DateTimeOffset? FirstMemberOutboundAt,
    Guid? FirstMemberId,
    DateTimeOffset? FirstInboundEverAt);

/// <summary>Una conversación cuyo primer entrante cayó en el periodo (spec 2026-10-28 D-4).</summary>
public sealed record NewContactRow(Guid ConversationId, ConversationChannel Channel, DateTimeOffset FirstInboundAt);

public sealed record ReportData(IReadOnlyList<ReportSessionRow> Sessions, IReadOnlyList<NewContactRow> NewContacts);

/// <summary>Cuántos eventos <c>Resolved</c> hubo en el periodo por canal y autor. <c>ChatbotId</c> <c>null</c> = los resolvió un miembro.</summary>
public sealed record ReportResolutionRow(ConversationChannel Channel, Guid? ChatbotId, int Resolved);

public sealed record MessagingReportPeriodDto(DateOnly From, DateOnly To, string GroupBy, string TimeZone);

public sealed record MessagingReportPreviousDto(DateOnly From, DateOnly To);

public sealed record NewContactsByChannelDto(int WhatsApp, int Instagram);

public sealed record MessagingReportTotalsDto(int Conversations, int NewContacts, int ReturningContacts, NewContactsByChannelDto NewContactsByChannel);

public sealed record MessagingReportTotalsPairDto(MessagingReportTotalsDto Current, MessagingReportTotalsDto Previous);

public sealed record MessagingReportPointDto(DateOnly Start, int Conversations, int NewContacts);

public sealed record ResponseBucketDto(string Key, int Count);

public sealed record ResponsePreviousDto(long? MedianSeconds, long? P90Seconds);

/// <summary>Un tiempo de respuesta. <c>null</c> en mediana y p90 cuando no hay sesiones respondidas (nunca 0).</summary>
public sealed record ResponseTimeDto(
    int AnsweredSessions,
    int UnansweredSessions,
    long? MedianSeconds,
    long? P90Seconds,
    ResponsePreviousDto Previous,
    IReadOnlyList<ResponseBucketDto> Buckets);

public sealed record MessagingReportAdvisorDto(
    Guid? MemberId,
    string DisplayName,
    int SessionsAttended,
    long? MedianAdvisorResponseSeconds,
    double? AnsweredUnder15mRatio,
    int UnansweredAssigned);

/// <summary>
/// Una barra de un chatbot en un canal. Decisión de BFF: <c>ResolvedTotal</c> cuenta todas las resoluciones del canal (miembros y cualquier
/// chatbot), que la pantalla no puede sumar porque nunca recibe las de los miembros, y por eso viaja en cada barra. <c>Ratio</c> (0..1) ya
/// viene dividido para que el cliente no repita la cuenta ni tenga que cuidar el cero: una barra existe sólo si hubo resoluciones.
/// </summary>
public sealed record MessagingReportChatbotChannelDto(string Channel, int ResolvedByChatbot, int ResolvedTotal, double Ratio);

/// <summary>
/// Lo que resolvió un chatbot. Sólo trae los canales donde resolvió al menos una: un canal ausente es «no resolvió ahí», sin barras en cero.
/// Decisión de BFF: los chatbots se borran físicamente y el backend es el único que sabe cuáles ya no existen, así que llegan fundidos en
/// una sola fila final con <c>ChatbotId</c> <c>null</c>; sin esto la pantalla tendría que agruparlos y ordenarlos.
/// </summary>
public sealed record MessagingReportChatbotDto(Guid? ChatbotId, string DisplayName, IReadOnlyList<MessagingReportChatbotChannelDto> Channels);

/// <summary>Spec 2026-10-28: el contrato de <c>GET /messaging/reports/summary</c>.</summary>
public sealed record MessagingReportSummaryDto(
    MessagingReportPeriodDto Period,
    MessagingReportPreviousDto Previous,
    bool BusinessHoursApplied,
    MessagingReportTotalsPairDto Totals,
    IReadOnlyList<MessagingReportPointDto> Series,
    ResponseTimeDto FirstResponse,
    ResponseTimeDto AdvisorResponse,
    IReadOnlyList<MessagingReportAdvisorDto> Advisors,
    IReadOnlyList<string> ChannelsWithData,
    IReadOnlyList<MessagingReportChatbotDto> Chatbots);
