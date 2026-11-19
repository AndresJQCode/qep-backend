using Microsoft.EntityFrameworkCore;
using Modules.Audit.Domain;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure.Persistence;

namespace Modules.Pipeline.UnitTests;

/// <summary>
/// Chatbot CRM action spec (2026-11-19): a deal the chatbot moves is audited as the system with source <c>chatbot</c>;
/// every other entry keeps <c>pipeline</c>. The tracked entry is inspected before saving: nothing touches a database.
/// </summary>
public sealed class PipelineAuditSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static PipelineDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused")
            .Options);

    [Fact]
    public void RecordWithSourceStoresIt()
    {
        using var context = NewContext();
        var recorder = new PipelineAuditRecorder(context);

        recorder.Record(
            Guid.CreateVersion7(), Guid.Empty, bySystem: true, PipelineAuditActions.DealMoved, PipelineAuditActions.DealResource,
            Guid.CreateVersion7(), Now, source: PipelineAuditSources.Chatbot);

        var entry = context.AuditEntries.Local.Single();
        Assert.Equal("chatbot", entry.Source);
        Assert.Equal(AuditActorType.System, entry.ActorType);
    }

    [Fact]
    public void RecordWithoutSourceStaysPipeline()
    {
        using var context = NewContext();
        var recorder = new PipelineAuditRecorder(context);

        recorder.Record(
            Guid.CreateVersion7(), Guid.CreateVersion7(), bySystem: false, PipelineAuditActions.DealMoved,
            PipelineAuditActions.DealResource, Guid.CreateVersion7(), Now);

        Assert.Equal("pipeline", context.AuditEntries.Local.Single().Source);
    }
}
