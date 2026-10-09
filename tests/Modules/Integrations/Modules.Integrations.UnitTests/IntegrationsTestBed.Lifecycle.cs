using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Integrations.UnitTests;

internal sealed partial class IntegrationsTestBed
{
    public RecordingEventPublisher Events { get; } = new();

    public TestConnectionHandler TestHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Protector, Tester, Modules, AuthorNames, context ?? Context(), Clock);

    public PauseConnectionHandler PauseHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Protector, Modules, AuthorNames, context ?? Context(), Clock);

    public ResumeConnectionHandler ResumeHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Protector, Tester, Modules, AuthorNames, context ?? Context(), Clock);

    public DeleteConnectionHandler DeleteHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, UnitOfWork, Audit, Events, Modules, context ?? Context(), Clock);
}

internal sealed record PublishedEvent(
    string EventName, Guid TenantId, Guid ConnectionId, string ProviderKey, DateTimeOffset OccurredAt);

internal sealed class RecordingEventPublisher : IConnectionEventPublisher
{
    public List<PublishedEvent> Published { get; } = [];

    public void Publish(string eventName, IntegrationConnection connection, DateTimeOffset occurredAt) =>
        Published.Add(new PublishedEvent(eventName, connection.TenantId, connection.Id, connection.ProviderKey, occurredAt));
}
