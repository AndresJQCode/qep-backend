namespace Modules.Hr.Domain;

public sealed class SalaryRecord : IEffectiveDated
{
    private SalaryRecord()
    {
    }

    internal SalaryRecord(
        SalaryRecordId id,
        EmploymentContractId contractId,
        DateOnly effectiveDate,
        decimal amount,
        bool teleworker,
        bool belowMinimumAcknowledged,
        DocumentRef? document,
        DateTimeOffset now)
    {
        Id = id;
        ContractId = contractId;
        EffectiveDate = effectiveDate;
        Amount = amount;
        Teleworker = teleworker;
        BelowMinimumAcknowledged = belowMinimumAcknowledged;
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public SalaryRecordId Id { get; private set; }

    public EmploymentContractId ContractId { get; private set; }

    public DateOnly EffectiveDate { get; private set; }

    public decimal Amount { get; private set; }

    public bool Teleworker { get; private set; }

    public bool BelowMinimumAcknowledged { get; private set; }

    public Guid? DocumentFileId { get; private set; }

    public string? DocumentFileName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal void Rewrite(decimal amount, bool teleworker, bool belowMinimumAcknowledged, DateTimeOffset now)
    {
        Amount = amount;
        Teleworker = teleworker;
        BelowMinimumAcknowledged = belowMinimumAcknowledged;
        UpdatedAt = now;
    }

    internal void SetDocument(DocumentRef? document, DateTimeOffset now)
    {
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
        UpdatedAt = now;
    }
}
