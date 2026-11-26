using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Plan 2026-11-17 Task 8: borrar una plantilla con imagen avisa la liberación antes del único guardado, así viaja en la misma
/// transacción que la eliminación de la fila. Si Meta no borra, no se avisa nada.</summary>
public sealed class DeleteTemplateHeaderImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingTemplateImageReleases Releases { get; } = new();

        public MessageTemplate Seed(Guid? fileId)
        {
            var template = MessageTemplate.Pending(
                Bed.TenantId, Bed.ConnectionId, "555", "promo_imagen", "es", "MARKETING", "NAMED", "APPROVED", "[]", MessageTemplateKind.Free, Bed.MemberId,
                Bed.Now.AddDays(-1));
            if (fileId is { } id)
            {
                template.AttachHeaderImage(id, $"https://public.test/{id}.png", Bed.Now.AddDays(-1));
            }

            Repository.Rows.Add(template);
            return template;
        }

        public Task<bool> DeleteAsync(MessageTemplate template)
        {
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            var handler = new DeleteTemplateHandler(
                Repository, Bed.Meta, Bed.Connections, UnitOfWork, new RecordingAudit(), Bed.TenantModules, executionContext, new FakeClock(Bed.Now), Releases);
            return handler.HandleAsync(new DeleteTemplateCommand(Bed.TenantId, Bed.ConnectionId, template.Id), Ct);
        }
    }

    [Fact]
    public async Task DeletingAnImageTemplatePublishesItsReleaseBeforeTheSave()
    {
        var rig = new Rig();
        var fileId = Guid.CreateVersion7();
        var template = rig.Seed(fileId);
        var savesAtPublish = -1;
        rig.Releases.OnPublish = () => savesAtPublish = rig.UnitOfWork.Saves;

        Assert.True(await rig.DeleteAsync(template));

        Assert.Empty(rig.Repository.Rows);
        Assert.Equal((rig.Bed.TenantId, fileId, rig.Bed.Now), Assert.Single(rig.Releases.Published));
        Assert.Equal(0, savesAtPublish);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task DeletingPublishesTheReleaseOfEveryRetiredImageToo()
    {
        var rig = new Rig();
        var currentId = Guid.CreateVersion7();
        var template = rig.Seed(currentId);
        var retiredA = Guid.CreateVersion7();
        var retiredB = Guid.CreateVersion7();
        rig.Repository.AddRetiredHeaderImage(MessageTemplateRetiredHeaderImage.Create(rig.Bed.TenantId, template.Id, retiredA, "https://public.test/a.png", rig.Bed.Now));
        rig.Repository.AddRetiredHeaderImage(MessageTemplateRetiredHeaderImage.Create(rig.Bed.TenantId, template.Id, retiredB, "https://public.test/b.png", rig.Bed.Now));
        var savesAtPublish = new List<int>();
        rig.Releases.OnPublish = () => savesAtPublish.Add(rig.UnitOfWork.Saves);

        Assert.True(await rig.DeleteAsync(template));

        Assert.Equal(
            new[] { currentId, retiredA, retiredB }.Order(),
            rig.Releases.Published.Select(published => published.FileId).Order());
        Assert.All(rig.Releases.Published, published => Assert.Equal((rig.Bed.TenantId, rig.Bed.Now), (published.TenantId, published.OccurredAt)));
        Assert.Equal([0, 0, 0], savesAtPublish);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task DeletingATemplateWithoutAnImagePublishesNothing()
    {
        var rig = new Rig();
        var template = rig.Seed(null);

        Assert.True(await rig.DeleteAsync(template));

        Assert.Empty(rig.Releases.Published);
    }

    [Fact]
    public async Task AFailedMetaDeletionPublishesNothing()
    {
        var rig = new Rig();
        var template = rig.Seed(Guid.CreateVersion7());
        rig.Bed.Meta.NextDelete = new MessagingGraphResult<DeletedTemplate>(null, new MessagingGraphFailure(500, null, "http_500"));

        await Assert.ThrowsAnyAsync<Exception>(() => rig.DeleteAsync(template));

        Assert.Single(rig.Repository.Rows);
        Assert.Empty(rig.Releases.Published);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }
}
