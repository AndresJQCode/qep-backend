using BuildingBlocks.Domain;

namespace Modules.Tenancy.Application;

public interface IOutboxWriter
{
    void Add(IDomainEvent domainEvent, string correlationId);
}
