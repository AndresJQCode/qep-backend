using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-10: cada teléfono de una tarjeta de contacto compartida trae, si el número es de un cliente activo y
/// no fusionado, ese cliente, y siempre el país y el número nacional para el formulario de «Agregar como cliente».</summary>
public sealed class SharedContactCustomerApiTests
{
    private const string Sender = "573110000000";

    private const string CardWaId = "573001234567";

    // La forma es la de messages[].contacts del webhook de Meta: name.formatted_name y phones[] con phone, wa_id y type.
    private const string LauraCard =
        """[{"name":{"formatted_name":"Laura Gómez"},"phones":[{"phone":"+57 300 123 4567","wa_id":"573001234567","type":"CELL"}]}]""";

    private sealed record Fixture(
        WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static long NextTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;

    private static long Tick() => Interlocked.Increment(ref NextTimestamp);

    private static async Task<Fixture> ArrangeAsync(
        TestDatabase database, Action<IServiceCollection>? configure = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var host = configure is null ? factory : factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(configure));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        _ = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using (var anonymous = host.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", Sender, "wamid.in.0", Tick(), "Hola"));
            await DrainDeliveriesAsync(host);
        }

        return new Fixture(
            host, connectionString, tenant, await ScalarAsync<Guid>(connectionString, "SELECT id FROM messaging.conversations"),
            CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ReadPermissions));
    }

    private static async Task PostCardAsync(Fixture f, string wamid, string contactsJson = LauraCard)
    {
        using var anonymous = f.Host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundContacts("111", Sender, wamid, Tick(), contactsJson));
        await DrainDeliveriesAsync(f.Host);
    }

    /// <summary>El primer teléfono del primer contacto del último mensaje de la página.</summary>
    private static async Task<JsonElement> LastPhoneAsync(Fixture f)
    {
        var page = await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), TestContext.Current.CancellationToken);
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        return items[^1].GetProperty("contacts")[0].GetProperty("phones")[0];
    }

    private static void AssertPhoneParts(JsonElement phone)
    {
        Assert.Equal("CO", phone.GetProperty("country").GetString());
        Assert.Equal("3001234567", phone.GetProperty("nationalNumber").GetString());
    }

    [Fact]
    public async Task ACardWhoseNumberIsAnActiveCustomerCarriesTheCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Host;
        var customerId = await CreateCustomerAsync(f.Host, f.Tenant, "Laura Gómez", "3001234567");
        await PostCardAsync(f, "wamid.card.1");

        var phone = await LastPhoneAsync(f);

        var customer = phone.GetProperty("customer");
        Assert.Equal(customerId, customer.GetProperty("id").GetGuid());
        Assert.Equal("Laura Gómez", customer.GetProperty("name").GetString());
        AssertPhoneParts(phone);
    }

    [Fact]
    public async Task ANumberOnlyAnInactiveCustomerHasCarriesNoCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Host;
        var customerId = await CreateCustomerAsync(f.Host, f.Tenant, "Laura Gómez", "3001234567");
        Assert.Equal(1, await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET is_active = false WHERE id = @id", ("id", customerId)));
        await PostCardAsync(f, "wamid.card.1");

        var phone = await LastPhoneAsync(f);

        Assert.Equal(JsonValueKind.Null, phone.GetProperty("customer").ValueKind);
        AssertPhoneParts(phone);
    }

    [Fact]
    public async Task ANumberOnlyAMergedCustomerHasCarriesNoCustomer()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Host;
        var ids = await CreateCustomersAsync(f.Host, f.Tenant, ("Laura Gómez", "3001234567"), ("Sobreviviente", "3009999999"));
        // is_active se deja en true: aísla el filtro de fusionados.
        Assert.Equal(1, await ExecuteAsync(
            f.ConnectionString, "UPDATE customers.customers SET merged_into_id = @other, merged_at = now() WHERE id = @id", ("id", ids[0]), ("other", ids[1])));
        await PostCardAsync(f, "wamid.card.1");

        var phone = await LastPhoneAsync(f);

        Assert.Equal(JsonValueKind.Null, phone.GetProperty("customer").ValueKind);
        AssertPhoneParts(phone);
    }

    [Fact]
    public async Task AnOldInactiveAndANewActiveCarryTheActive()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Host;
        var ids = await CreateCustomersAsync(f.Host, f.Tenant, ("Viejo", "3001234567"), ("Nuevo", "3001234567"));
        Assert.Equal(1, await ExecuteAsync(
            f.ConnectionString,
            "UPDATE customers.customers SET is_active = false, created_at = created_at - interval '1 day' WHERE id = @id", ("id", ids[0])));
        await PostCardAsync(f, "wamid.card.1");

        var phone = await LastPhoneAsync(f);

        Assert.Equal(ids[1], phone.GetProperty("customer").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AfterCreatingTheCustomerTheNextReadCarriesIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Host;
        await PostCardAsync(f, "wamid.card.1");

        var before = await LastPhoneAsync(f);
        var customerId = await CreateCustomerAsync(f.Host, f.Tenant, "Laura Gómez", "3001234567");
        var after = await LastPhoneAsync(f);

        Assert.Equal(JsonValueKind.Null, before.GetProperty("customer").ValueKind);
        Assert.Equal(customerId, after.GetProperty("customer").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ThePageResolvesEveryCardInOneCall()
    {
        await using var database = await StartDatabaseAsync();
        var calls = new SharedPhoneCalls();
        var f = await ArrangeAsync(database, services =>
        {
            var directory = services.Single(descriptor => descriptor.ServiceType == typeof(IMessagingCustomerDirectory));
            services.Remove(directory);
            services.AddScoped(provider => CountingDirectory.Wrap(
                (IMessagingCustomerDirectory)ActivatorUtilities.CreateInstance(provider, directory.ImplementationType!), calls));
        });
        using var host = f.Host;

        // 1) Sólo el texto del Arrange: ninguna tarjeta, ninguna llamada.
        var textOnly = await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), TestContext.Current.CancellationToken);
        // El hilo ya trae el texto y el evento CustomerCreated que escribe la ingesta; ninguno es una tarjeta.
        var baseline = textOnly.GetProperty("items").GetArrayLength();
        Assert.All(textOnly.GetProperty("items").EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("contacts").ValueKind));
        Assert.Equal(0, calls.Count);

        // 2) Dos tarjetas que comparten un número: una llamada, con los tres números sin repetir.
        await PostCardAsync(f, "wamid.card.1", """[{"name":{"formatted_name":"Laura Gómez"},"phones":[{"phone":"+57 300 123 4567","wa_id":"573001234567","type":"CELL"},{"phone":"+1 415 555 2671","wa_id":"14155552671","type":"WORK"}]}]""");
        await PostCardAsync(f, "wamid.card.2", """[{"name":{"formatted_name":"Pedro"},"phones":[{"phone":"+57 300 123 4567","wa_id":"573001234567","type":"CELL"},{"phone":"+34 612 34 56 78","wa_id":"34612345678","type":"CELL"}]}]""");
        calls.Reset();

        var withCards = await f.Client.GetFromJsonAsync<JsonElement>(MessagesUrl(f.Tenant.TenantId, f.ConversationId), TestContext.Current.CancellationToken);

        Assert.Equal(baseline + 2, withCards.GetProperty("items").GetArrayLength());
        Assert.Equal(1, calls.Count);
        Assert.Equal(["14155552671", "34612345678", CardWaId], calls.WaIds.Order(StringComparer.Ordinal));
    }

    /// <summary>Cuántas veces se llamó a <c>ResolveSharedPhonesAsync</c> y con qué <c>waId</c>.</summary>
    public sealed class SharedPhoneCalls
    {
        private readonly List<string> waIds = [];

        public int Count { get; private set; }

        public IReadOnlyList<string> WaIds => waIds;

        public void Record(IReadOnlyCollection<string> requested)
        {
            Count++;
            waIds.AddRange(requested);
        }

        public void Reset()
        {
            Count = 0;
            waIds.Clear();
        }
    }

    /// <summary>Delega todo en el directorio real y cuenta las llamadas a <c>ResolveSharedPhonesAsync</c>.</summary>
    public class CountingDirectory : DispatchProxy
    {
        private IMessagingCustomerDirectory inner = null!;
        private SharedPhoneCalls calls = null!;

        internal static IMessagingCustomerDirectory Wrap(IMessagingCustomerDirectory inner, SharedPhoneCalls calls)
        {
            var proxy = Create<IMessagingCustomerDirectory, CountingDirectory>();
            var counting = (CountingDirectory)(object)proxy;
            counting.inner = inner;
            counting.calls = calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IMessagingCustomerDirectory.ResolveSharedPhonesAsync))
            {
                calls.Record((IReadOnlyCollection<string>)args![1]!);
            }

            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
