using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-14 §4.1: el espejo contra Postgres de verdad. Ida y vuelta, los dos índices únicos traducidos por nombre,
/// el marcador de sincronización y la consulta de campañas activas.</summary>
public sealed class MessageTemplatePersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId)
    {
        public Guid TenantId => Tenant.TenantId;
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId);
    }

    private static async Task<T> InScopeAsync<T>(QepApiFactory factory, Func<IMessageTemplateRepository, IMessagingUnitOfWork, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(
            scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>());
    }

    private static MessageTemplate Mirrored(Fixture f, string metaId, string name, string language = "es") =>
        MessageTemplate.Mirrored(f.TenantId, f.ConnectionId, metaId, name, language, "MARKETING", "NAMED", "APPROVED", null, "[]", Now);

    [Fact]
    public async Task APendingAndAMirroredTemplateRoundTripWithTheirColumns()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var member = Guid.CreateVersion7();

        var (pendingId, mirroredId) = await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            var pending = MessageTemplate.Pending(f.TenantId, f.ConnectionId, "900", "promo", "es", "MARKETING", "NAMED", "PENDING",
                """[{"type":"BODY","text":"Hola {{first_name}}"}]""", MessageTemplateKind.Free, member, Now);
            var mirrored = MessageTemplate.Mirrored(f.TenantId, f.ConnectionId, "901", "aviso", "es", "UTILITY", "POSITIONAL", "APPROVED", "NONE", "[]", Now);
            repository.Add(pending);
            repository.Add(mirrored);
            await unitOfWork.SaveChangesAsync(Ct);
            return (pending.Id, mirrored.Id);
        });

        await InScopeAsync(f.Factory, async (repository, _) =>
        {
            var all = await repository.ListAsync(f.TenantId, f.ConnectionId, Ct);
            Assert.Equal(["aviso", "promo"], all.Select(template => template.Name));
            var promo = await repository.FindAsync(f.TenantId, f.ConnectionId, pendingId, Ct);
            Assert.NotNull(promo);
            Assert.Equal(("900", MessageTemplateKind.Free, "PENDING", member), (promo.MetaTemplateId, promo.Kind, promo.Status, promo.CreatedByMemberId));
            // jsonb normaliza espacios y reordena las claves de cada objeto: se compara el JSON y no el texto.
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{"type":"BODY","text":"Hola {{first_name}}"}]"""), JsonNode.Parse(promo.ComponentsJson)));
            Assert.Equal(mirroredId, (await repository.FindByMetaIdAsync(f.TenantId, f.ConnectionId, "901", Ct))!.Id);
            Assert.Equal(mirroredId, (await repository.FindByNameAsync(f.TenantId, f.ConnectionId, "aviso", "es", Ct))!.Id);
            Assert.Single(await repository.ListApprovedAsync(f.TenantId, f.ConnectionId, Ct));
            Assert.Null(await repository.FindQuotationAsync(f.TenantId, f.ConnectionId, Ct));
            // Otro tenant con los mismos ids no ve nada.
            Assert.Empty(await repository.ListAsync(Guid.CreateVersion7(), f.ConnectionId, Ct));
            return 0;
        });
    }

    [Fact]
    public async Task ARepeatedNameAndLanguageIs422NameTakenByIndexName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "1", "promo"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "2", "promo"));
            return await unitOfWork.SaveChangesAsync(Ct);
        }));

        Assert.Equal(MessagingErrorCodes.TemplateNameTaken, exception.Code);
        // Mismo nombre en otro idioma sí cabe.
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "3", "promo", "en_US"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });
    }

    [Fact]
    public async Task ARepeatedMetaIdIsAConcurrencyConflict()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "1", "a"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Mirrored(f, "1", "b"));
            return await unitOfWork.SaveChangesAsync(Ct);
        }));
    }

    private static MessageTemplate Quotation(Guid tenantId, Guid connectionId, string metaId, string name) =>
        MessageTemplate.Pending(tenantId, connectionId, metaId, name, "es", "UTILITY", "NAMED", "PENDING", "[]", MessageTemplateKind.Quotation, null, Now);

    private static MessageTemplate Billing(Guid tenantId, Guid connectionId, string metaId, string name, MessageTemplateKind kind) =>
        MessageTemplate.Pending(tenantId, connectionId, metaId, name, "es", "UTILITY", "POSITIONAL", "PENDING", "[]", kind, null, Now);

    // Cobros recurrentes §5: una fila por tipo de aviso y conexión (UX_message_templates_billing); los dos tipos conviven y el que
    // pierde la carrera es billing_exists.
    [Fact]
    public async Task OneBillingTemplatePerKindInTheSameConnectionAndTheSecondIs422BillingExists()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Billing(f.TenantId, f.ConnectionId, "1", "qep_cobro_recordatorio", MessageTemplateKind.BillingReminder));
            repository.Add(Billing(f.TenantId, f.ConnectionId, "2", "qep_cobro_vencido", MessageTemplateKind.BillingOverdue));
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Billing(f.TenantId, f.ConnectionId, "3", "qep_cobro_recordatorio_v2", MessageTemplateKind.BillingReminder));
            return await unitOfWork.SaveChangesAsync(Ct);
        }));

        Assert.Equal(MessagingErrorCodes.TemplateBillingExists, exception.Code);
    }

    // Fix de la Task 8: dos primeros aprovisionamientos a la vez crean qep_cotizacion y qep_cotizacion_v2 en Meta y los dos insertan su
    // Quotation. El índice único parcial UX_message_templates_quotation deja una sola por conexión y el que pierde es quotation_exists.
    [Fact]
    public async Task ASecondQuotationTemplateInTheSameConnectionIs422QuotationExistsByIndexName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Quotation(f.TenantId, f.ConnectionId, "1", "qep_cotizacion"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        var exception = await Assert.ThrowsAsync<MessagingDomainException>(() => InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Quotation(f.TenantId, f.ConnectionId, "2", "qep_cotizacion_v2"));
            return await unitOfWork.SaveChangesAsync(Ct);
        }));

        Assert.Equal(MessagingErrorCodes.TemplateQuotationExists, exception.Code);
        // Otra conexión del mismo tenant tiene la suya, y las que no son Quotation no cuentan.
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            repository.Add(Quotation(f.TenantId, Guid.CreateVersion7(), "3", "qep_cotizacion"));
            repository.Add(Mirrored(f, "4", "qep_cotizacion_v3"));
            repository.Add(Mirrored(f, "5", "promo"));
            return await unitOfWork.SaveChangesAsync(Ct);
        });
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.message_templates WHERE kind = 'Quotation' AND tenant_id = @t AND connection_id = @c", ("t", f.TenantId), ("c", f.ConnectionId)));
        Assert.Equal(4L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.message_templates WHERE tenant_id = @t", ("t", f.TenantId)));
    }

    [Fact]
    public async Task TheSyncMarkerIsNullUntilMarkedAndThenUpserts()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        Assert.Null(await InScopeAsync(f.Factory, (repository, _) => repository.LastSyncAsync(f.TenantId, f.ConnectionId, Ct)));

        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            await repository.MarkSyncedAsync(f.TenantId, f.ConnectionId, Now, Ct);
            return await unitOfWork.SaveChangesAsync(Ct);
        });
        await InScopeAsync(f.Factory, async (repository, unitOfWork) =>
        {
            await repository.MarkSyncedAsync(f.TenantId, f.ConnectionId, Now.AddHours(1), Ct);
            return await unitOfWork.SaveChangesAsync(Ct);
        });

        Assert.Equal(Now.AddHours(1), await InScopeAsync(f.Factory, (repository, _) => repository.LastSyncAsync(f.TenantId, f.ConnectionId, Ct)));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.template_mirror_syncs"));
    }

    // Enmienda de la Task 5, punto 5: dos primeros llenados de la misma conexión leyeron «sin marcador» y los dos lo insertan. El que
    // pierde el PK de template_mirror_syncs es concurrency.conflict (no un 500), y lo que su sincronización dejó pendiente se descarta:
    // el catálogo sigue leyendo el espejo que llenó el otro y el handler que lo llamó guarda lo suyo sin volver a chocar.
    [Fact]
    public async Task TwoFirstSyncsOfTheSameConnectionAreAConcurrencyConflictAndTheLoserCanStillSave()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        using var loser = f.Factory.Services.CreateScope();
        var repository = loser.ServiceProvider.GetRequiredService<IMessageTemplateRepository>();
        var unitOfWork = loser.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>();
        repository.Add(Mirrored(f, "2", "b"));
        await repository.MarkSyncedAsync(f.TenantId, f.ConnectionId, Now, Ct);
        await InScopeAsync(f.Factory, async (winner, winnerUnitOfWork) =>
        {
            winner.Add(Mirrored(f, "1", "a"));
            await winner.MarkSyncedAsync(f.TenantId, f.ConnectionId, Now, Ct);
            return await winnerUnitOfWork.SaveChangesAsync(Ct);
        });

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(() => unitOfWork.SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", exception.Code);
        Assert.Equal(0, await unitOfWork.SaveChangesAsync(Ct));
        Assert.Equal(["a"], (await repository.ListApprovedAsync(f.TenantId, f.ConnectionId, Ct)).Select(template => template.Name));
    }

    [Fact]
    public async Task AScheduledOrSendingCampaignReferencesTheTemplateADraftOrFinishedOneDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var member = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        foreach (var (name, status) in new[] { ("promo", "Scheduled"), ("aviso", "Sending"), ("borrador", "Draft"), ("vieja", "Completed") })
        {
            await ExecuteAsync(f.ConnectionString,
                """
                INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category, parameters,
                    audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
                VALUES (@id, @tenantId, @connectionId, @name, @name, 'es', 'MARKETING', '{}'::jsonb, ARRAY[@tag]::uuid[], ARRAY[]::uuid[], @status, @member, now(), now(), 1)
                """,
                ("id", Guid.CreateVersion7()), ("tenantId", f.TenantId), ("connectionId", f.ConnectionId), ("name", name), ("tag", Guid.CreateVersion7()),
                ("status", status), ("member", member));
        }

        await InScopeAsync(f.Factory, async (repository, _) =>
        {
            Assert.True(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "promo", "es", Ct));
            Assert.True(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "aviso", "es", Ct));
            Assert.False(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "borrador", "es", Ct));
            Assert.False(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "vieja", "es", Ct));
            Assert.False(await repository.IsReferencedByActiveCampaignAsync(f.TenantId, f.ConnectionId, "promo", "en_US", Ct));
            return 0;
        });
    }
}
