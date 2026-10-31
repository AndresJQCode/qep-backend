using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.ServiceContracts.Application;
using Modules.Tenancy.Domain;
using Npgsql;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// <c>GET/PUT /customers/{customerId}/profile</c> y <c>GET /assignees</c> (spec cobros-recurrentes §3.1 y §4), de punta a
/// punta por HTTP: política, tenant de la ruta, módulo habilitado, reglas del perfil y auditoría atómica.
/// </summary>
public sealed class ServiceContractsProfileApiTests
{
    private static readonly Guid Tenant = Guid.Parse(ServiceContractsApiHarness.TenantId);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ProfileUrl(Guid customerId, string tenantId = ServiceContractsApiHarness.TenantId) =>
        $"{ServiceContractsUrl(tenantId)}/customers/{customerId}/profile";

    private static object Body(string channel, Guid assigneeMemberId) => new { channel, assigneeMemberId };

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task PutCreatesTheProfileAndGetReadsItBack()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", admin), Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var created = await put.Content.ReadFromJsonAsync<ProfileDto>(Ct);
        Assert.Equal(customerId, created!.CustomerId);
        Assert.Equal("WhatsApp", created.Channel);
        Assert.Equal(new MemberRefDto(admin, "Ana Admin", true), created.Assignee);
        Assert.Equal(1, created.Version);

        var get = await manager.GetAsync(ProfileUrl(customerId), Ct);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created, await get.Content.ReadFromJsonAsync<ProfileDto>(Ct));
    }

    [Fact]
    public async Task APureReaderCanGetButNotPut()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);
        using var reader = CreateReader(factory);
        await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", admin), Ct);

        var get = await reader.GetAsync(ProfileUrl(customerId), Ct);
        var put = await reader.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", admin), Ct);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task PutAgainOverwritesWithoutIfMatch()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        var biller = await SeedMemberAsync(factory, "bea@example.com", "Bea Facturación", "billing");
        using var manager = CreateManager(factory);
        await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", admin), Ct);

        var second = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", biller), Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var profile = await second.Content.ReadFromJsonAsync<ProfileDto>(Ct);
        Assert.Equal("Email", profile!.Channel);
        Assert.Equal("Bea Facturación", profile.Assignee.DisplayName);
        Assert.Equal(2, profile.Version);
    }

    [Fact]
    public async Task PutWithARemovedMemberAnswers422AssigneeInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var removed = await SeedMemberAsync(factory, "rita@example.com", "Rita Quitada", "admin", removed: true);
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", removed), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("billing.profile.assignee_invalid", await CodeOfAsync(put));
    }

    // Owner 2026-10-29: el encargado tiene que poder marcar pagos.
    [Fact]
    public async Task PutWithAnActiveMemberWithoutManageAnswers422AssigneeInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var advisor = await SeedMemberAsync(factory, "caro@example.com", "Caro Asesora", "advisor");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", advisor), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("billing.profile.assignee_invalid", await CodeOfAsync(put));
    }

    [Fact]
    public async Task PutWithAnUnknownMemberAnswers422AssigneeInvalid()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("billing.profile.assignee_invalid", await CodeOfAsync(put));
    }

    [Fact]
    public async Task PutWhatsAppForACustomerWithoutPhoneAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Sin teléfono", phoneE164: null, email: "sin@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", admin), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("billing.profile.whatsapp_requires_phone", await CodeOfAsync(put));
    }

    [Fact]
    public async Task PutEmailForACustomerWithoutAddressAnswers422()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Sin correo", phoneE164: "+573001234567", email: null);
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", admin), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("billing.profile.email_requires_address", await CodeOfAsync(put));
    }

    [Fact]
    public async Task PutForAnUnknownOrForeignCustomerAnswers422CustomerNotFound()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, ServiceContractsApiHarness.OtherTenantId);
        var foreign = await SeedCustomerAsync(
            factory, Guid.Parse(ServiceContractsApiHarness.OtherTenantId), "Ajeno", "+573009998877", "ajeno@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var unknown = await manager.PutAsJsonAsync(ProfileUrl(Guid.CreateVersion7()), Body("Email", admin), Ct);
        var other = await manager.PutAsJsonAsync(ProfileUrl(foreign), Body("Email", admin), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("billing.customer.not_found", await CodeOfAsync(unknown));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Equal("billing.customer.not_found", await CodeOfAsync(other));
    }

    [Theory]
    [InlineData("Sms")]
    [InlineData("whatsapp")]
    [InlineData("")]
    public async Task PutWithAnInvalidChannelAnswers422ValidationFailed(string channel)
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body(channel, admin), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(put));
    }

    [Fact]
    public async Task PutWithoutAnAssigneeAnswers422ValidationFailed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        using var manager = CreateManager(factory);

        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), new { channel = "Email" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(put));
    }

    [Fact]
    public async Task GetWithoutProfileAnswers404WithCode()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        using var manager = CreateManager(factory);

        var get = await manager.GetAsync(ProfileUrl(customerId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("billing.profile.not_found", await CodeOfAsync(get));
    }

    [Fact]
    public async Task GetDoesNotReachTheProfileOfAnotherTenantsCustomer()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, ServiceContractsApiHarness.OtherTenantId);
        var otherTenant = Guid.Parse(ServiceContractsApiHarness.OtherTenantId);
        var foreign = await SeedCustomerAsync(factory, otherTenant, "Ajeno", "+573009998877", "ajeno@example.com");
        var foreignAdmin = await SeedMemberAsync(
            factory, "otro@example.com", "Otro Admin", "admin", tenantId: ServiceContractsApiHarness.OtherTenantId);
        using var otherManager = CreateClient(
            factory, ServiceContractsApiHarness.OtherSubjectId, ServiceContractsApiHarness.OtherTenantId, ServiceContractsPermissions.Read, ServiceContractsPermissions.Manage);
        using var manager = CreateManager(factory);
        var put = await otherManager.PutAsJsonAsync(
            ProfileUrl(foreign, ServiceContractsApiHarness.OtherTenantId), Body("Email", foreignAdmin), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // El mismo id de cliente bajo el tenant propio: la búsqueda está acotada al tenant, no existe.
        var get = await manager.GetAsync(ProfileUrl(foreign), Ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("billing.profile.not_found", await CodeOfAsync(get));
    }

    [Fact]
    public async Task AnotherTenantAnswers403NotFound404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, ServiceContractsApiHarness.OtherTenantId);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        // Quien opera en el otro tenant y apunta a la ruta de éste: 403, nunca 404 (no revela si el cliente existe).
        using var outsider = CreateClient(
            factory, ServiceContractsApiHarness.OtherSubjectId, ServiceContractsApiHarness.OtherTenantId, ServiceContractsPermissions.Read, ServiceContractsPermissions.Manage);

        var get = await outsider.GetAsync(ProfileUrl(customerId), Ct);
        var put = await outsider.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", admin), Ct);
        var assignees = await outsider.GetAsync($"{ServiceContractsUrl()}/assignees", Ct);

        foreach (var response in new[] { get, put, assignees })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("authorization.denied", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task WithTheModuleOffAnswersABare403()
    {
        var (database, factory) = await OpenAsync(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.ServiceContracts));
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        var get = await manager.GetAsync(ProfileUrl(customerId), Ct);
        var put = await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", admin), Ct);
        var assignees = await manager.GetAsync($"{ServiceContractsUrl()}/assignees", Ct);

        foreach (var response in new[] { get, put, assignees })
        {
            // ModuleEntitlementMask quita el permiso antes del handler (owner, 2026-10-29): 403 pelado, sin código de módulo.
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("tenancy.module_not_enabled", await BodyAsync(response));
        }
    }

    [Fact]
    public async Task AssigneesListsOnlyActiveMembersWhoCanManageSortedByName()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var zoe = await SeedMemberAsync(factory, "zoe@example.com", "Zoe Admin", "admin");
        var bea = await SeedMemberAsync(factory, "bea@example.com", "Bea Facturación", "billing");
        await SeedMemberAsync(factory, "caro@example.com", "Caro Asesora", "advisor");
        await SeedMemberAsync(factory, "rita@example.com", "Rita Quitada", "admin", removed: true);
        using var reader = CreateReader(factory);

        var response = await reader.GetAsync($"{ServiceContractsUrl()}/assignees", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assignees = await response.Content.ReadFromJsonAsync<AssigneesDto>(Ct);
        Assert.Equal(
            [new MemberRefDto(bea, "Bea Facturación", true), new MemberRefDto(zoe, "Zoe Admin", true)],
            assignees!.Items);
    }

    [Fact]
    public async Task SavingTheProfileWritesAnAtomicAuditEntry()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panadería La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", admin), Ct);
        await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("Email", admin), Ct);

        Assert.Equal(2, await CountAuditEntriesAsync(factory, "billing.profile.updated", customerId.ToString()));
    }

    [Fact]
    public async Task ARejectedPutWritesNoAuditEntry()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var customerId = await SeedCustomerAsync(factory, Tenant, "Sin teléfono", phoneE164: null, email: "sin@example.com");
        var admin = await SeedMemberAsync(factory, "ana@example.com", "Ana Admin", "admin");
        using var manager = CreateManager(factory);

        await manager.PutAsJsonAsync(ProfileUrl(customerId), Body("WhatsApp", admin), Ct);

        Assert.Equal(0, await CountAuditEntriesAsync(factory, "billing.profile.updated", customerId.ToString()));
    }

    // El adaptador de F1: publicar acumula la entrada en ServiceContractsDbContext, así que commitea con el cambio de negocio y
    // se pierde con él. Resolverlo por el puerto es lo que reciben los handlers.
    [Fact]
    public async Task TheAuditPublisherRecordsIntoTheSameUnitOfWork()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var resourceId = Guid.CreateVersion7().ToString();
        var occurredAt = new DateTimeOffset(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);

        await using (var discarded = factory.Services.CreateAsyncScope())
        {
            discarded.ServiceProvider.GetRequiredService<IServiceContractsAuditPublisher>().Publish(
                Tenant, Guid.Parse(SubjectId), ServiceContractsAuditActions.ProfileUpdated,
                ServiceContractsAuditActions.Resources.Profile, resourceId, "success", ["channel"], occurredAt);
            // Sin SaveChanges: la entrada no se escribe por su cuenta.
        }

        Assert.Equal(0, await CountAuditEntriesAsync(factory, ServiceContractsAuditActions.ProfileUpdated, resourceId));

        await using (var committed = factory.Services.CreateAsyncScope())
        {
            committed.ServiceProvider.GetRequiredService<IServiceContractsAuditPublisher>().Publish(
                Tenant, Guid.Parse(SubjectId), ServiceContractsAuditActions.ProfileUpdated,
                ServiceContractsAuditActions.Resources.Profile, resourceId, "success", ["channel", "assigneeMemberId"], occurredAt);
            await committed.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(1, await CountAuditEntriesAsync(factory, ServiceContractsAuditActions.ProfileUpdated, resourceId));
        var entry = await ReadAuditEntryAsync(factory, resourceId);
        Assert.Equal((Tenant, "profile", "billing", "[\"channel\", \"assigneeMemberId\"]"), entry);
    }

    private static async Task<(Guid? TenantId, string ResourceType, string Source, string ChangedFields)> ReadAuditEntryAsync(
        QepApiFactory factory, string resourceId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var select = new NpgsqlCommand(
            "SELECT tenant_id, resource_type, source, changed_fields::text FROM audit.entries WHERE resource_id = @id",
            connection);
        select.Parameters.AddWithValue("id", resourceId);
        await using var reader = await select.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }
}
