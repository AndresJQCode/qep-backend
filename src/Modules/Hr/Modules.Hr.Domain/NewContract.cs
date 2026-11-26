namespace Modules.Hr.Domain;

/// <summary>Un contrato por crear con sus dos registros iniciales (salario y cargo), que rigen desde <c>Terms.StartDate</c>.</summary>
public sealed record NewContract(
    EmploymentContractId Id,
    ContractTerms Terms,
    DocumentRef? Document,
    SalaryRecordId SalaryId,
    decimal Amount,
    bool Teleworker,
    bool AcknowledgeBelowMinimum,
    AssignmentRecordId AssignmentId,
    PositionId PositionId,
    AreaId? AreaId,
    SiteId SiteId,
    WorkplaceId WorkplaceId);
