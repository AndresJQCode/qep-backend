using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Storage.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-11-17, «Closing the residual gaps»: el adaptador de Bootstrapper que le dice al barrido de Storage qué imágenes de encabezado
/// usa todavía una fila de <c>messaging.message_templates</c>. Contra Postgres de verdad porque es una consulta sobre el mapeo de EF.
/// </summary>
public sealed class TemplateImageReferencesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    // Review Focus 5: el id que usa otro tenant no cuenta, y un id sin fila tampoco.
    [Fact]
    public async Task FindsOnlyTheTenantsReferencedIds()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantA = await RegisterTenantAsync(factory);
        var tenantB = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenantA.TenantId);
        await EnableMessagingAsync(connectionString, tenantB.TenantId);
        var connectionA = await SeedWhatsAppConnectionAsync(factory, tenantA.TenantId, "Ventas", "111", "222");
        var connectionB = await SeedWhatsAppConnectionAsync(factory, tenantB.TenantId, "Ventas", "333", "444");
        var fileA = Guid.CreateVersion7();
        var fileB = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>();
            repository.Add(WithImage(tenantA.TenantId, connectionA, "900", fileA));
            repository.Add(WithImage(tenantB.TenantId, connectionB, "901", fileB));
            await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var references = scope.ServiceProvider.GetRequiredService<ITemplateImageReferences>();

            var referencedByA = await references.FindReferencedAsync(tenantA.TenantId, [fileA, fileB, unused], Ct);
            var referencedByB = await references.FindReferencedAsync(tenantB.TenantId, [fileA, fileB, unused], Ct);
            var none = await references.FindReferencedAsync(tenantA.TenantId, [], Ct);

            Assert.Equal([fileA], referencedByA);
            Assert.Equal([fileB], referencedByB);
            Assert.Empty(none);
        }
    }

    // Spec 2026-11-20: una imagen retirada sigue referenciada (Meta puede mostrarla), y sólo para su tenant.
    [Fact]
    public async Task RetiredImagesCountAsReferenced()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantA = await RegisterTenantAsync(factory);
        var tenantB = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenantA.TenantId);
        await EnableMessagingAsync(connectionString, tenantB.TenantId);
        var connectionA = await SeedWhatsAppConnectionAsync(factory, tenantA.TenantId, "Ventas", "111", "222");
        var connectionB = await SeedWhatsAppConnectionAsync(factory, tenantB.TenantId, "Ventas", "333", "444");
        var current = Guid.CreateVersion7();
        var retiredA = Guid.CreateVersion7();
        var retiredB = Guid.CreateVersion7();
        var unused = Guid.CreateVersion7();

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>();
            var templateA = WithImage(tenantA.TenantId, connectionA, "900", retiredA);
            repository.AddRetiredHeaderImage(templateA.ReplaceHeaderImage(current, $"https://public.test/{current}.png", Now)!);
            var templateB = WithImage(tenantB.TenantId, connectionB, "901", retiredB);
            repository.AddRetiredHeaderImage(templateB.ReplaceHeaderImage(Guid.CreateVersion7(), "https://public.test/x.png", Now)!);
            repository.Add(templateA);
            repository.Add(templateB);
            await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var references = scope.ServiceProvider.GetRequiredService<ITemplateImageReferences>();
            var repository = scope.ServiceProvider.GetRequiredService<IMessageTemplateRepository>();

            var referencedByA = await references.FindReferencedAsync(tenantA.TenantId, [current, retiredA, retiredB, unused], Ct);

            Assert.Equal(new[] { current, retiredA }.Order(), referencedByA.Order());
            Assert.True(await repository.IsHeaderImageInUseAsync(tenantA.TenantId, retiredA, Ct));
            Assert.False(await repository.IsHeaderImageInUseAsync(tenantA.TenantId, retiredB, Ct));
        }
    }

    private static MessageTemplate WithImage(Guid tenantId, Guid connectionId, string metaId, Guid fileId)
    {
        var template = MessageTemplate.Pending(tenantId, connectionId, metaId, "promo", "es", "MARKETING", "NAMED", "PENDING",
            """[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola"}]""", MessageTemplateKind.Free, null, Now);
        template.AttachHeaderImage(fileId, $"https://public.test/{fileId}.png", Now);
        return template;
    }
}
