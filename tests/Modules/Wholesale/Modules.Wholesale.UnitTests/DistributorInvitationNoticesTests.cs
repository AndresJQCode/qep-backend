using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

/// <summary>
/// Spec 2026-11-18 (aviso por WhatsApp): lo que el procesador del aviso lee y escribe del distribuidor. Un evento de una
/// <c>generation</c> vieja no se manda ni pisa el estado (D-4), y registrar el resultado no sube <c>Version</c> (D-3).
/// </summary>
public sealed class DistributorInvitationNoticesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
    private const string Hash1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Hash2 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Phone = "+573001234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Distributor Invite() =>
        Distributor.Invite(
            Tenant, "ana@distri.co", "Distribuciones Norte SAS", "CO", "NIT", "900123456", "Ana Pérez", PriceListId.New(),
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Hash1, Guid.CreateVersion7(), Phone,
            Guid.CreateVersion7(), Now);

    [Fact]
    public async Task AStaleGenerationIsNotCurrent()
    {
        var repository = new FakeDistributorRepository();
        var distributor = repository.Seed(Invite());
        distributor.ResendInvitation(Hash2, noticeQueued: true, Now.AddHours(1));
        var notices = new DistributorInvitationNotices(repository);

        var stale = await notices.FindCurrentAsync(Tenant, distributor.Id.Value, 0, Ct);
        var current = await notices.FindCurrentAsync(Tenant, distributor.Id.Value, 1, Ct);

        Assert.Null(stale);
        Assert.Same(distributor, current);
    }

    [Fact]
    public async Task ADeletedOrAcceptedDistributorIsNotCurrent()
    {
        var repository = new FakeDistributorRepository();
        var accepted = repository.Seed(Invite());
        accepted.Accept(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(1));
        var deleted = Invite();
        var notices = new DistributorInvitationNotices(repository);

        Assert.Null(await notices.FindCurrentAsync(Tenant, accepted.Id.Value, 0, Ct));
        Assert.Null(await notices.FindCurrentAsync(Tenant, deleted.Id.Value, 0, Ct));
        // Otro tenant no lo encuentra aunque tenga el id.
        Assert.Null(await notices.FindCurrentAsync(Guid.CreateVersion7(), accepted.Id.Value, 0, Ct));
    }

    [Fact]
    public async Task RecordAppliesOnlyTheCurrentGeneration()
    {
        var repository = new FakeDistributorRepository();
        var distributor = repository.Seed(Invite());
        distributor.ResendInvitation(Hash2, noticeQueued: true, Now.AddHours(1));
        var version = distributor.Version;
        var notices = new DistributorInvitationNotices(repository);

        var stale = await notices.RecordAsync(Tenant, distributor.Id.Value, 0, InvitationNoticeStatus.Sent, null, Ct);

        Assert.False(stale);
        Assert.Equal(InvitationNoticeStatus.Pending, distributor.Invitation.NoticeStatus);

        var current = await notices.RecordAsync(
            Tenant, distributor.Id.Value, 1, InvitationNoticeStatus.Failed, "template_not_approved", Ct);

        Assert.True(current);
        Assert.Equal(
            ((InvitationNoticeStatus?)InvitationNoticeStatus.Failed, (string?)"template_not_approved"),
            (distributor.Invitation.NoticeStatus, distributor.Invitation.NoticeFailureCode));
        // D-3: el resultado no sube la versión, así el If-Match del formulario abierto sigue valiendo.
        Assert.Equal(version, distributor.Version);
        // No guarda: guarda el procesador, con el inbox, en un solo SaveChanges.
        Assert.Equal(0, repository.Writes);
    }
}
