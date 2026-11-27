namespace Modules.Hr.Domain;

public sealed class AssignmentRecord : IEffectiveDated
{
    private AssignmentRecord()
    {
    }

    internal AssignmentRecord(
        AssignmentRecordId id,
        EmploymentContractId contractId,
        DateOnly effectiveDate,
        PositionId positionId,
        AreaId? areaId,
        SiteId siteId,
        WorkplaceId workplaceId,
        DocumentRef? document,
        DateTimeOffset now)
    {
        Id = id;
        ContractId = contractId;
        EffectiveDate = effectiveDate;
        PositionId = positionId;
        AreaId = areaId;
        SiteId = siteId;
        WorkplaceId = workplaceId;
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
        CreatedAt = now;
    }

    public AssignmentRecordId Id { get; private set; }

    public EmploymentContractId ContractId { get; private set; }

    public DateOnly EffectiveDate { get; private set; }

    public PositionId PositionId { get; private set; }

    public AreaId? AreaId { get; private set; }

    public SiteId SiteId { get; private set; }

    public WorkplaceId WorkplaceId { get; private set; }

    public Guid? DocumentFileId { get; private set; }

    public string? DocumentFileName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public AssignmentEntry ToEntry() => new(EffectiveDate, PositionId, AreaId, SiteId, WorkplaceId);

    /// <summary>Reescribe fecha, cargo, área, sede y centro. Las reglas de vecinos las revisa el contrato antes.</summary>
    internal void Rewrite(AssignmentEntry entry)
    {
        EffectiveDate = entry.EffectiveDate;
        PositionId = entry.PositionId;
        AreaId = entry.AreaId;
        SiteId = entry.SiteId;
        WorkplaceId = entry.WorkplaceId;
    }

    internal void SetDocument(DocumentRef? document)
    {
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
    }
}
