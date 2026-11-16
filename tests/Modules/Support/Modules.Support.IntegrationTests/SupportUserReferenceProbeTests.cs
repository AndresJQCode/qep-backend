using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La sonda de retención de Soporte: Identity no borra a un usuario huérfano mientras alguna de sus membresías sea la responsable
/// de un ticket o haya actuado en su traza. Comparten host: cada prueba siembra su tenant y sus usuarios, y la sonda pregunta por
/// membresías que sólo existen en ese tenant.
/// </summary>
public sealed class SupportUserReferenceProbeTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static readonly SupportClockContext Clock =
        new(new WeekdayCalendar(), TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAssigneeKeepsTheUser()
    {
        var (factory, tenant) = await NewTenantAsync();
        var assignee = await SeedMemberAsync(factory, $"assignee-{Guid.CreateVersion7():N}@example.com", "Ana", "admin", tenantId: tenant.ToString());
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var assigneeUser = await UserOfAsync(factory, assignee);
        Assert.False(await RetainsAsync(factory, assigneeUser));

        await SaveTicketAsync(factory, tenant, assignee, actor);

        Assert.True(await RetainsAsync(factory, assigneeUser));
    }

    [Fact]
    public async Task AnEventActorKeepsTheUser()
    {
        var (factory, tenant) = await NewTenantAsync();
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var actorUser = await UserOfAsync(factory, actor);
        Assert.False(await RetainsAsync(factory, actorUser));

        await SaveTicketAsync(factory, tenant, assignee: null, actor);

        Assert.True(await RetainsAsync(factory, actorUser));
    }

    [Fact]
    public async Task WithoutReferencesTheUserIsFree()
    {
        var (factory, tenant) = await NewTenantAsync();
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var bystander = await SeedMemberAsync(factory, $"bystander-{Guid.CreateVersion7():N}@example.com", "Caro", "admin", tenantId: tenant.ToString());
        var bystanderUser = await UserOfAsync(factory, bystander);

        await SaveTicketAsync(factory, tenant, assignee: null, actor);

        Assert.False(await RetainsAsync(factory, bystanderUser));
    }

    // La respuesta se guarda sin RecordReply: así sólo la fila de ticket_replies referencia al autor, y la traza no lo retiene.
    [Fact]
    public async Task ARepliedMemberIsReferenced()
    {
        var (factory, tenant) = await NewTenantAsync();
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var author = await SeedMemberAsync(factory, $"author-{Guid.CreateVersion7():N}@example.com", "Dani", "admin", tenantId: tenant.ToString());
        var authorUser = await UserOfAsync(factory, author);
        var ticketId = await SaveTicketAsync(factory, tenant, assignee: null, actor);
        Assert.False(await RetainsAsync(factory, authorUser));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>().Add(SupportTicketReply.Start(
                tenant, ticketId, Guid.CreateVersion7(), SupportReplyChannel.Email, SupportReplyDelivery.Email, "Ya revisamos tu caso.",
                null, author, Now.AddMinutes(5)));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, authorUser));
    }

    [Fact]
    public async Task AMemberWhoDismissedAMessageIsReferenced()
    {
        var (factory, tenant) = await NewTenantAsync();
        var member = await SeedMemberAsync(factory, $"dismiss-{Guid.CreateVersion7():N}@example.com", "Eva", "admin", tenantId: tenant.ToString());
        var memberUser = await UserOfAsync(factory, member);
        Assert.False(await RetainsAsync(factory, memberUser));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var unclassified = SupportUnclassifiedMessage.Create(tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Now, Now);
            unclassified.Dismiss(member, Now.AddMinutes(1));
            scope.ServiceProvider.GetRequiredService<ISupportUnclassifiedRepository>().Add(unclassified);
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, memberUser));
    }

    // Fase 3: el autor de una versión del formulario o de los textos legales queda en created_by (CLAUDE.md: toda columna con id de
    // membresía en support entra en la sonda).
    [Fact]
    public async Task AFormOrLegalTextAuthorIsReferenced()
    {
        var (factory, tenant) = await NewTenantAsync();
        var formAuthor = await SeedMemberAsync(factory, $"form-{Guid.CreateVersion7():N}@example.com", "Fer", "admin", tenantId: tenant.ToString());
        var textAuthor = await SeedMemberAsync(factory, $"text-{Guid.CreateVersion7():N}@example.com", "Gabi", "admin", tenantId: tenant.ToString());
        var formUser = await UserOfAsync(factory, formAuthor);
        var textUser = await UserOfAsync(factory, textAuthor);
        Assert.False(await RetainsAsync(factory, formUser));
        Assert.False(await RetainsAsync(factory, textUser));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportFormRepository>().Add(SupportFormVersion.Create(
                Guid.CreateVersion7(), tenant, 1, false, true, true, true, true, false, null, null, null, formAuthor, Now));
            scope.ServiceProvider.GetRequiredService<ISupportLegalTextRepository>().Add(SupportLegalTextVersion.Create(
                Guid.CreateVersion7(), tenant, 1, "Autorizo el tratamiento de mis datos.", null, "https://example.com/politica", null,
                textAuthor, Now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, formUser));
        Assert.True(await RetainsAsync(factory, textUser));
    }

    // Spec 2026-11-15 §5.4 (plan N23): ticket_notes.author_member_id is a membership id, so the note's author keeps the user.
    [Fact]
    public async Task ANoteAuthorKeepsTheUser()
    {
        var (factory, tenant) = await NewTenantAsync();
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var author = await SeedMemberAsync(factory, $"note-{Guid.CreateVersion7():N}@example.com", "Hugo", "admin", tenantId: tenant.ToString());
        var authorUser = await UserOfAsync(factory, author);
        var ticketId = await SaveTicketAsync(factory, tenant, assignee: null, actor);
        Assert.False(await RetainsAsync(factory, authorUser));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportTicketNoteRepository>().Add(SupportTicketNote.Create(
                Guid.CreateVersion7(), tenant, ticketId, author, "Nota interna.", [], Now.AddMinutes(5)));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, authorUser));
    }

    private async Task<(QepApiFactory Factory, Guid Tenant)> NewTenantAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var tenantId = NewTenantId();
        await SeedTenantAsync(factory, tenantId);
        return (factory, Guid.Parse(tenantId));
    }

    private static async Task<SupportTicketId> SaveTicketAsync(QepApiFactory factory, Guid tenant, Guid? assignee, Guid actor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        var type = (await scope.ServiceProvider.GetRequiredService<ISupportTicketTypeRepository>().ListAsync(tenant, Ct))[0];
        var ticket = SupportTicket.CreateManual(
            SupportTicketId.New(), tenant, 1, type, "Asunto", "Descripción", SupportTicketPriority.Medium,
            ManualIntakeMedium.Phone, SupportReplyChannel.Email, null,
            new TicketRequester("Ana", "Gómez", null, null, null, "ana@example.com", null, null, "CO", null),
            assignee, actor, Now, Clock);
        scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>().Add(ticket);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return ticket.Id;
    }

    private static async Task<Guid> UserOfAsync(QepApiFactory factory, Guid membershipId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Memberships
            .Where(membership => membership.Id == new MembershipId(membershipId))
            .Select(membership => membership.UserId)
            .SingleAsync(Ct);
    }

    private static async Task<bool> RetainsAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(candidate => candidate.Source == "support");
        return await probe.HasReferencesAsync(userId, Ct);
    }

    private sealed class WeekdayCalendar : ISupportBusinessCalendar
    {
        public bool IsBusinessDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
