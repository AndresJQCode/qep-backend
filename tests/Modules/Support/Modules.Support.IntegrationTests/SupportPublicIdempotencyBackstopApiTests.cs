using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportPublicHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The backstop of the idempotent filing (plan L27; final review, Block C): when both key lookups miss the ticket (the one before
/// Turnstile and the one under the counter's lock), the unique index <c>IX_tickets_tenant_client_request</c> still refuses the second
/// ticket. <c>SupportUnitOfWork</c> translates it by name to <see cref="SupportClientRequestTakenException"/> and the handler answers with
/// the winner: one ticket, the number rolled back, no second email. The lookups are blinded by a repository decorator that hides the
/// next N <c>FindByClientRequestAsync</c> results; everything else reaches the real repository and the real database.
/// </summary>
public sealed class SupportPublicIdempotencyBackstopApiTests(SupportIdempotencyBackstopHost host)
    : IClassFixture<SupportIdempotencyBackstopHost>
{
    [Fact]
    public async Task TheUniqueIndexAnswersARetryBothLookupsMissed()
    {
        var factory = await host.StartTestAsync();
        var tenant = NewTenantId();
        var slug = await SeedPublishedTenantAsync(factory, tenant);
        var typeId = await FirstTypeIdAsync(factory, tenant);
        var cityId = await AnyCityIdAsync(factory);
        var email = $"ana-{Guid.CreateVersion7():N}@example.com";
        var key = Guid.NewGuid();

        var first = await FileAsync(factory, slug, NamedBody(typeId, cityId, email, clientRequestId: key));
        host.Blindfold.Hide(2);
        var retry = await FileAsync(factory, slug, NamedBody(typeId, cityId, email, clientRequestId: key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(0, host.Blindfold.Remaining);
        Assert.Equal("SOP-000001", (await JsonAsync(first)).GetProperty("number").GetString());
        Assert.Equal("SOP-000001", (await JsonAsync(retry)).GetProperty("number").GetString());
        Assert.Equal(1L, await ScalarAsync(
            factory, "SELECT count(*) FROM support.tickets WHERE tenant_id = @tenant", ("tenant", Guid.Parse(tenant))));
        Assert.Equal(2L, await ScalarAsync(
            factory, "SELECT count(*) FROM support.legal_acceptances WHERE tenant_id = @tenant", ("tenant", Guid.Parse(tenant))));
        Assert.Single(host.Emails.To(email));

        var next = await FileAsync(factory, slug, NamedBody(typeId, cityId, $"ana-{Guid.CreateVersion7():N}@example.com"));
        Assert.Equal("SOP-000002", (await JsonAsync(next)).GetProperty("number").GetString());
    }

    private static async Task<HttpResponseMessage> FileAsync(QepApiFactory factory, string slug, object body)
    {
        using var client = VisitorClient(factory, NextIp());
        return await PostJsonAsync(client, PublicUrl(slug, "/tickets"), body);
    }
}

/// <summary><see cref="SupportPublicHost"/> with the ticket repository behind <see cref="BlindClientRequestRepository"/>.</summary>
public sealed class SupportIdempotencyBackstopHost : SupportPublicHost
{
    internal ClientRequestBlindfold Blindfold { get; } = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        var real = services.Single(descriptor => descriptor.ServiceType == typeof(ISupportTicketRepository));
        var implementation = real.ImplementationType!;
        services.Remove(real);
        services.Add(new ServiceDescriptor(implementation, implementation, real.Lifetime));
        services.Add(new ServiceDescriptor(
            typeof(ISupportTicketRepository),
            provider => BlindClientRequestRepository.Wrap((ISupportTicketRepository)provider.GetRequiredService(implementation), Blindfold),
            real.Lifetime));
    }
}

/// <summary>How many of the next key lookups come back empty. Shared by every request of the host.</summary>
public sealed class ClientRequestBlindfold
{
    private int _remaining;

    public int Remaining => Volatile.Read(ref _remaining);

    public void Hide(int lookups) => Volatile.Write(ref _remaining, lookups);

    public bool TryTake()
    {
        while (true)
        {
            var current = Volatile.Read(ref _remaining);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _remaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }
}

/// <summary>Forwards every call to the real repository, except a blinded <c>FindByClientRequestAsync</c>, which answers null.</summary>
public class BlindClientRequestRepository : DispatchProxy
{
    private ISupportTicketRepository _inner = null!;
    private ClientRequestBlindfold _blindfold = null!;

    internal static ISupportTicketRepository Wrap(ISupportTicketRepository inner, ClientRequestBlindfold blindfold)
    {
        var proxy = Create<ISupportTicketRepository, BlindClientRequestRepository>();
        var blind = (BlindClientRequestRepository)(object)proxy;
        blind._inner = inner;
        blind._blindfold = blindfold;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(ISupportTicketRepository.FindByClientRequestAsync) && _blindfold.TryTake())
        {
            return Task.FromResult<SupportTicket?>(null);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
