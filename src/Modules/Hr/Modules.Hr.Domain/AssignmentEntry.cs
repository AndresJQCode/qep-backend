namespace Modules.Hr.Domain;

/// <summary>Un cargo y lugar de trabajo nuevo; Application arma <c>SiteId</c> desde el centro.</summary>
public sealed record AssignmentEntry(
    DateOnly EffectiveDate,
    PositionId PositionId,
    AreaId? AreaId,
    SiteId SiteId,
    WorkplaceId WorkplaceId);
