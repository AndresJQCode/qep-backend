namespace Modules.Hr.Domain;

public sealed class ContractRenewal
{
    private ContractRenewal()
    {
    }

    internal ContractRenewal(
        ContractRenewalId id,
        EmploymentContractId contractId,
        DateOnly previousEndDate,
        DateOnly newEndDate,
        DocumentRef? document,
        DateTimeOffset now)
    {
        Id = id;
        ContractId = contractId;
        PreviousEndDate = previousEndDate;
        NewEndDate = newEndDate;
        IsShort = FixedTermLimits.IsShort(previousEndDate, newEndDate);
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
        CreatedAt = now;
    }

    public ContractRenewalId Id { get; private set; }

    public EmploymentContractId ContractId { get; private set; }

    public DateOnly PreviousEndDate { get; private set; }

    public DateOnly NewEndDate { get; private set; }

    /// <summary>Menos de un año sobre el fin anterior: cuenta para la regla de las tres prórrogas cortas.</summary>
    public bool IsShort { get; private set; }

    public Guid? DocumentFileId { get; private set; }

    public string? DocumentFileName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal void SetDocument(DocumentRef? document)
    {
        DocumentFileId = document?.FileId;
        DocumentFileName = document?.FileName;
    }
}
