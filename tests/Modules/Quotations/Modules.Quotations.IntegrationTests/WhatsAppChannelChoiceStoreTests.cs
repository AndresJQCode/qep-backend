using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La elección del canal contra la base real (spec 2026-10-21 Decisión 9): lo que el SQL de <c>RememberAsync</c> hace
/// cuando dos envíos eligen a la vez, que ninguna prueba de la API ve.
/// </summary>
public sealed class WhatsAppChannelChoiceStoreTests
{
    // Review Focus 2: el segundo en llegar no pisa la elección que el primero acaba de guardar.
    [Fact]
    public async Task RememberDoesNotOverwriteAChoiceMadeMeanwhile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWhatsAppChannelChoiceStore>();
        var tenantId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var token = TestContext.Current.CancellationToken;

        var first = await store.RememberAsync(tenantId, a, previous: null, memberId, now, token);
        var second = await store.RememberAsync(tenantId, b, previous: null, memberId, now, token);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(a, await store.FindAsync(tenantId, token));

        var replaced = await store.RememberAsync(tenantId, b, previous: a, memberId, now, token);

        Assert.True(replaced);
        Assert.Equal(b, await store.FindAsync(tenantId, token));
    }
}
