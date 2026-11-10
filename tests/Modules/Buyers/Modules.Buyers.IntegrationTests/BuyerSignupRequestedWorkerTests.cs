using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
using Modules.Buyers.Infrastructure;
using Modules.Buyers.Infrastructure.Signup;
using Npgsql;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// El alta desde el checkout del lado de Buyers (D7): el worker consume <c>store.buyer-signup-requested.v1</c> sembrado en
/// <c>platform.outbox_messages</c> con el JSON del contrato. Los workers de fondo están apagados y se drena a mano. Necesitan Docker.
/// </summary>
public sealed class BuyerSignupRequestedWorkerTests
{
    private const string Verify = "buyers.email-verification-requested.v1";
    private const string Host = "acme.lulocrm.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Payload(Guid orderId, string email = "ana@correo.com", string passwordHash = "hash-del-mensaje") =>
        JsonSerializer.Serialize(
            new
            {
                orderId,
                tenantId = Guid.CreateVersion7(),
                clientRequestId = Guid.CreateVersion7(),
                storeHost = Host,
                storeName = "Tienda Demo",
                visitorIp = "203.0.113.9",
                email,
                passwordHash,
                billing = new
                {
                    name = "Ana Pérez",
                    identificationType = "cc",
                    identificationNumber = "123456",
                    phone = "3001234567",
                    email,
                    address = "Calle 1 # 2-3",
                    country = "co",
                    departmentId = Guid.CreateVersion7(),
                    cityId = Guid.CreateVersion7(),
                    cityName = (string?)null,
                },
                shipping = (object?)null,
            },
            JsonSerializerOptions.Web);

    private static async Task<Guid> SeedAsync(BuyersApiHarness.BuyersApi api, string payloadJson)
    {
        var id = Guid.CreateVersion7();
        var connectionString = api.Factory.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @name, @payload::jsonb, 'test', now(), 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", BuyerSignupRequestedMessage.EventName);
        command.Parameters.AddWithValue("payload", payloadJson);
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private static Task DrainAsync(BuyersApiHarness.BuyersApi api) =>
        ActivatorUtilities.CreateInstance<BuyerSignupRequestedWorker>(api.Factory.Services).DrainAsync(Ct);

    private static Task<long> CountAsync(BuyersApiHarness.BuyersApi api, string sql, params object[] args) =>
        api.ScalarAsync<long>(sql, args);

    [Fact]
    public async Task ProcessingTheSameMessageTwiceCreatesOneAccountAndOneEmail()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await SeedAsync(api, Payload(Guid.CreateVersion7()));

        await DrainAsync(api);
        await api.ScalarAsync<long>("WITH d AS (DELETE FROM buyers.inbox_messages RETURNING 1) SELECT count(*) FROM d");
        await DrainAsync(api);

        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
        Assert.Single(await api.OutboxPayloadsAsync(Verify));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.processed_signups"));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.inbox_messages WHERE processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task TwoMessagesForTheSameOrderRequestApplyOnce()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var orderId = Guid.CreateVersion7();
        await SeedAsync(api, Payload(orderId));
        await SeedAsync(api, Payload(orderId, passwordHash: "otro-hash"));

        await DrainAsync(api);

        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
        Assert.Single(await api.OutboxPayloadsAsync(Verify));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.consents"));
        Assert.Equal(2, await CountAsync(api, "SELECT count(*) FROM buyers.inbox_messages WHERE processed_at IS NOT NULL"));
    }

    private sealed class CountingHasher(IBuyerPasswordHasher inner) : IBuyerPasswordHasher
    {
        private int _calls;

        public int Calls => _calls;

        public string Hash(string password)
        {
            Interlocked.Increment(ref _calls);
            return inner.Hash(password);
        }

        public BuyerPasswordCheck Verify(string? hash, string password)
        {
            Interlocked.Increment(ref _calls);
            return inner.Verify(hash, password);
        }
    }

    [Fact]
    public async Task TheAccountUsesTheHashFromTheMessage()
    {
        var hasher = new CountingHasher(new AspNetCoreBuyerPasswordHasher());
        await using var api = await BuyersApiHarness.OpenAsync(services =>
        {
            services.RemoveAll<IBuyerPasswordHasher>();
            services.AddSingleton<IBuyerPasswordHasher>(hasher);
        });
        await SeedAsync(api, Payload(Guid.CreateVersion7(), passwordHash: "AQAAAAIAAYagAAAAEhash-del-mensaje"));

        await DrainAsync(api);

        Assert.Equal(
            "AQAAAAIAAYagAAAAEhash-del-mensaje",
            await api.ScalarAsync<string>("SELECT password_hash FROM buyers.accounts WHERE email = 'ana@correo.com'"));
        Assert.Equal(0, hasher.Calls);
        // El perfil sale de los bloques del pedido, con los códigos como los guarda la consola.
        Assert.Equal("CC", await api.ScalarAsync<string>("SELECT data->'billing'->>'identificationType' FROM buyers.profiles"));
        Assert.Equal("CO", await api.ScalarAsync<string>("SELECT data->'billing'->>'country' FROM buyers.profiles"));
        Assert.True(await api.ScalarAsync<bool>("SELECT (data->>'shippingSameAsBilling')::boolean FROM buyers.profiles"));
        // El consentimiento lleva el host y la IP del visitante que viajaron en el mensaje.
        Assert.Equal(Host, await api.ScalarAsync<string>("SELECT store_host FROM buyers.consents"));
        Assert.Equal("203.0.113.9", await api.ScalarAsync<string>("SELECT visitor_ip FROM buyers.consents"));
        Assert.Equal($"https://{Host}/cuenta/politica", await api.ScalarAsync<string>("SELECT policy_url FROM buyers.consents"));
    }

    [Fact]
    public async Task AMalformedMessageIsRecordedAsExhaustedAfterThreeAttempts()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        var id = await SeedAsync(api, """{"orderId":"not-a-guid"}""");

        for (var attempt = 0; attempt < BuyerSignupRequestedWorker.MaxAttempts + 1; attempt++)
        {
            await DrainAsync(api);
            // El lease de 2 minutos vence a mano para que el siguiente drenado lo reintente.
            await api.ScalarAsync<long>(
                "WITH u AS (UPDATE buyers.inbox_messages SET claimed_until = now() - interval '1 minute' WHERE processed_at IS NULL RETURNING 1) SELECT count(*) FROM u");
        }

        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.inbox_messages WHERE message_id = $1 AND processed_at IS NOT NULL", id));
        Assert.Equal(4, await api.ScalarAsync<int>("SELECT attempts FROM buyers.inbox_messages WHERE message_id = $1", id));
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
    }

    [Fact]
    public async Task AMessageWithAnEmailThatDoesNotNormalizeIsClosedWithoutAnAccount()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await SeedAsync(api, Payload(Guid.CreateVersion7(), email: "sin-arroba"));

        await DrainAsync(api);

        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.inbox_messages WHERE processed_at IS NOT NULL AND attempts = 1"));
        Assert.Equal(0, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
    }

    [Fact]
    public async Task AVerifiedEmailOnlyGetsTheExistingAccountNotice()
    {
        await using var api = await BuyersApiHarness.OpenAsync();
        await api.RegisterVerifiedAsync("ana@correo.com");
        var hashBefore = await api.ScalarAsync<string>("SELECT password_hash FROM buyers.accounts");
        var verifyBefore = (await api.OutboxPayloadsAsync(Verify)).Count;
        await SeedAsync(api, Payload(Guid.CreateVersion7(), email: " Ana@Correo.com "));

        await DrainAsync(api);

        Assert.Single(await api.OutboxPayloadsAsync("buyers.existing-account-notice.v1"));
        Assert.Equal(verifyBefore, (await api.OutboxPayloadsAsync(Verify)).Count);
        Assert.Equal(hashBefore, await api.ScalarAsync<string>("SELECT password_hash FROM buyers.accounts"));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.processed_signups"));
    }

    /// <summary>
    /// R7: una alta que pierde la carrera del email descarta lo pendiente y reintenta por el camino de email existente. La marca de
    /// procesado y el cierre del inbox también se descartan, así que tienen que volver a agregarse en el reintento.
    /// </summary>
    [Fact]
    public async Task ASignupThatLosesTheEmailRaceStillMarksItsMessageProcessed()
    {
        var blind = new BlindSwitch();
        await using var api = await BuyersApiHarness.OpenAsync(services =>
        {
            var original = services.Single(descriptor => descriptor.ServiceType == typeof(IBuyerAccountRepository));
            services.Remove(original);
            services.AddScoped<IBuyerAccountRepository>(provider => new BlindOnceAccounts(
                (IBuyerAccountRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!), blind));
        });
        // Una cuenta sin verificar del mismo email: el worker no la ve en su primera búsqueda, como si otra alta la hubiera
        // creado entre su búsqueda y su guardado.
        var registered = await api.PostAsync(
            "/register",
            new { email = "ana@correo.com", password = "una-contrasena-larga", acceptsDataPolicy = true, turnstileToken = "ok", profile = (object?)null });
        registered.EnsureSuccessStatusCode();
        var orderId = Guid.CreateVersion7();
        await SeedAsync(api, Payload(orderId, passwordHash: "hash-del-checkout"));
        blind.Armed = true;

        await DrainAsync(api);

        Assert.False(blind.Armed);
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.accounts"));
        Assert.Equal("hash-del-checkout", await api.ScalarAsync<string>("SELECT password_hash FROM buyers.accounts"));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.processed_signups WHERE order_id = $1", orderId));
        Assert.Equal(1, await CountAsync(api, "SELECT count(*) FROM buyers.inbox_messages WHERE processed_at IS NOT NULL"));
    }

    private sealed class BlindSwitch
    {
        public volatile bool Armed;
    }

    /// <summary>Armado, esconde una sola vez la cuenta que existe: la primera búsqueda por email devuelve null.</summary>
    private sealed class BlindOnceAccounts(IBuyerAccountRepository inner, BlindSwitch blind) : IBuyerAccountRepository
    {
        public async Task<BuyerAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        {
            var found = await inner.FindByEmailAsync(normalizedEmail, cancellationToken);
            if (blind.Armed)
            {
                blind.Armed = false;
                return null;
            }

            return found;
        }

        public Task<BuyerAccount?> FindAsync(Guid id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

        public void Add(BuyerAccount account) => inner.Add(account);

        public void Remove(BuyerAccount account) => inner.Remove(account);
    }
}
