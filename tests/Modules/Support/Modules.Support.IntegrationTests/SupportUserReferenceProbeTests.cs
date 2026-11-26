using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

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

    // Spec 2026-11-22: el creador de una tarea también retiene al usuario. Se borra la traza para que sólo created_by_member_id lo
    // referencie: el responsable es otro y el actor del evento Created desaparece.
    [Fact]
    public async Task TheCreatorOfATicketIsRetained()
    {
        var (factory, tenant) = await NewTenantAsync();
        var creator = await SeedMemberAsync(factory, $"creator-{Guid.CreateVersion7():N}@example.com", "Eva", "admin", tenantId: tenant.ToString());
        var assignee = await SeedMemberAsync(factory, $"assignee-{Guid.CreateVersion7():N}@example.com", "Ana", "admin", tenantId: tenant.ToString());
        var creatorUser = await UserOfAsync(factory, creator);
        Assert.False(await RetainsAsync(factory, creatorUser));
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, tenant, creator, assignee);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<SupportDbContext>().Database;
            await database.ExecuteSqlAsync($"DELETE FROM support.ticket_events WHERE ticket_id = {task.Id.Value}", Ct);
        }

        Assert.True(await RetainsAsync(factory, creatorUser));
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
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        // The phase 3 legal texts have no writer any more (spec 2026-11-15 §5.4): the old row goes in by raw SQL.
        await ExecuteAsync(
            factory,
            """
            INSERT INTO support.legal_text_versions
                (id, tenant_id, version, authorization_text, policy_text, policy_url, terms_text, created_at, created_by)
            VALUES (gen_random_uuid(), @tenant, 1, 'Autorizo el tratamiento de mis datos.', NULL, 'https://example.com/politica', NULL,
                    now(), @author)
            """,
            ("tenant", tenant), ("author", textAuthor));

        Assert.True(await RetainsAsync(factory, formUser));
        Assert.True(await RetainsAsync(factory, textUser));
    }

    // Spec 2026-11-15 §5.4 (plan L22): legal_document_versions.created_by is a membership id, so the author keeps the user.
    [Fact]
    public async Task ALegalDocumentAuthorIsReferenced()
    {
        var (factory, tenant) = await NewTenantAsync();
        var author = await SeedMemberAsync(factory, $"doc-{Guid.CreateVersion7():N}@example.com", "Iris", "admin", tenantId: tenant.ToString());
        var user = await UserOfAsync(factory, author);
        Assert.False(await RetainsAsync(factory, user));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportLegalDocumentRepository>().Add(SupportLegalDocumentVersion.Create(
                Guid.CreateVersion7(), tenant, SupportLegalDocumentKind.Terms, 1, new SanitizedHtml("<p>Términos.</p>", true), null, null,
                author, Now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, user));
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

    // Fase 4b (H3, owner 2026-11-16, Q-7): firma la respuesta formal el responsable del ticket, y su membresía queda en
    // ticket_reply_documents.signer_member_id. El autor de la respuesta es otro miembro, para que sólo la firma retenga al firmante.
    [Fact]
    public async Task TheSignerOfAFormalReplyHoldsTheUser()
    {
        var (factory, tenant) = await NewTenantAsync();
        var actor = await SeedMemberAsync(factory, $"actor-{Guid.CreateVersion7():N}@example.com", "Beto", "admin", tenantId: tenant.ToString());
        var signer = await SeedMemberAsync(factory, $"signer-{Guid.CreateVersion7():N}@example.com", "Julia", "admin", tenantId: tenant.ToString());
        var signerUser = await UserOfAsync(factory, signer);
        var ticketId = await SaveTicketAsync(factory, tenant, assignee: null, actor);
        Assert.False(await RetainsAsync(factory, signerUser));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var reply = SupportTicketReply.Start(
                tenant, ticketId, Guid.CreateVersion7(), SupportReplyChannel.Email, SupportReplyDelivery.Email,
                "Te enviamos la respuesta formal.", null, actor, Now.AddMinutes(5));
            var replies = scope.ServiceProvider.GetRequiredService<ISupportReplyRepository>();
            replies.Add(reply);
            replies.AddDocument(SupportReplyDocument.Create(
                reply, "Decidimos devolver el dinero.", null, signer, "Julia", "Coordinadora", [],
                $"support/tenants/{tenant:N}/replies/2026/11/{Guid.CreateVersion7():N}.pdf", "Respuesta-SOP-000001.pdf",
                [0x25, 0x50, 0x44, 0x46], new DateOnly(2026, 11, 13), Now.AddMinutes(5)));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, signerUser));
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
