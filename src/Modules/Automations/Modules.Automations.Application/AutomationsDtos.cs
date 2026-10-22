using System.Text.Json;

namespace Modules.Automations.Application;

// Spec 2026-10-21 §4.4. Los instantes viajan como instante; el frontend los formatea con la zona del tenant.

public sealed record ChatbotConnectionDto(Guid Id, string Name, string Channel, string Handle);

public sealed record ChatbotTodayDto(int Handled, int HandedOff);

public sealed record ChatbotSummaryDto(
    Guid Id,
    string Name,
    string Description,
    string Status,
    string Trigger,
    IReadOnlyList<ChatbotConnectionDto> Connections,
    int? PublishedVersion,
    DateTimeOffset? PublishedAt,
    bool HasDraftChanges,
    ChatbotTodayDto Today,
    long Version);

public sealed record ChatbotListResponse(IReadOnlyList<ChatbotSummaryDto> Items);

public sealed record ChatbotDraftDto(JsonElement Flow, JsonElement Layout, DateTimeOffset UpdatedAt, Guid UpdatedBy);

public sealed record ChatbotPublishedDto(int Number, DateTimeOffset At);

public sealed record FlowIssueDto(string Severity, string Code, string? NodeId, string Message);

public sealed record ServedByDto(Guid ChatbotId, string Name);

public sealed record SelectableConnectionDto(
    Guid Id, string Name, string Channel, string Handle, string Status, ServedByDto? ServedBy, string? BusinessHoursName);

public sealed record ExternalApiRefDto(Guid Id, string Name, string Status);

public sealed record ChatbotEditorDto(
    ChatbotSummaryDto Chatbot,
    IReadOnlyList<string> Keywords,
    string RestartKeyword,
    int MaxAttempts,
    ChatbotDraftDto Draft,
    ChatbotPublishedDto? Published,
    IReadOnlyList<FlowIssueDto> Issues,
    IReadOnlyList<SelectableConnectionDto> Connections,
    IReadOnlyList<ExternalApiRefDto> ExternalApis,
    IReadOnlyList<string> Variables);

public sealed record DraftSavedDto(long Version, DateTimeOffset UpdatedAt, IReadOnlyList<FlowIssueDto> Issues);
