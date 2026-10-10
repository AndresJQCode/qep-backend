using System.Text;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, <c>PUT /connections/{id}</c>: If-Match, secreto ausente conserva (D5), prueba sólo
/// si cambió algo que importa (P9), y una prueba fallida no toca nada.
/// </summary>
public sealed class UpdateConnectionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UpdateConnectionCommand Update(
        IntegrationsTestBed bed,
        IntegrationConnection connection,
        string name = "WhatsApp sede norte",
        string? fromNumber = IntegrationsTestBed.FromNumber,
        Dictionary<string, string?>? secrets = null,
        long? version = null) =>
        new(
            bed.TenantId,
            connection.Id,
            version ?? connection.Version,
            name,
            new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = fromNumber },
            secrets ?? []);

    private static string StoredToken(IntegrationConnection connection) =>
        Encoding.UTF8.GetString(connection.Secrets[0].Protected.Ciphertext).Split('|')[2];

    [Fact]
    public async Task RenamingOnlyDoesNotCallTheProvider()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.UpdateHandler().HandleAsync(Update(bed, connection, name: "Sede sur"), Ct);

        Assert.Equal("Sede sur", response.Name);
        Assert.Equal(2, response.Version);
        Assert.Empty(bed.Tester.Calls);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.updated", audit.Action);
        Assert.Equal(["name"], audit.ChangedFields);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // D5 y Review Focus 3: ausente, null, vacío o sólo espacios conservan la clave guardada.
    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("spaces")]
    public async Task AnAbsentOrBlankSecretKeepsTheStoredOne(string shape)
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var secrets = shape switch
        {
            "absent" => new Dictionary<string, string?>(),
            "null" => new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = null },
            "empty" => new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "" },
            _ => new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "   " },
        };

        await bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999", secrets: secrets), Ct);

        Assert.Equal(IntegrationsTestBed.Token, Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
        Assert.Equal(["fromNumber"], bed.Audit.Entries[0].ChangedFields);
        Assert.Equal("integrations.connection.verified", bed.Audit.Entries[1].Action);
    }

    [Fact]
    public async Task ANewSecretIsTestedAndReplacesTheStoredOne()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await bed.UpdateHandler().HandleAsync(
            Update(bed, connection, secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "nuevo-token" }),
            Ct);

        Assert.Equal("nuevo-token", Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal("nuevo-token", StoredToken(connection));
        Assert.Equal(["apiToken"], bed.Audit.Entries[0].ChangedFields);
    }

    [Fact]
    public async Task RejectedCredentialsLeaveTheConnectionUntouched()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.UpdateHandler().HandleAsync(
            Update(bed, connection, secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "otro" }), Ct));

        Assert.Equal("integrations.connection.credentials_rejected", error.Code);
        Assert.Equal(1, connection.Version);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    // D3: «no pude verificar» también bloquea el guardado de una edición.
    [Fact]
    public async Task AnUnreachableProviderLeavesTheConnectionUntouched()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.UpdateHandler().HandleAsync(
            Update(bed, connection, fromNumber: "573009999999"), Ct));

        Assert.Equal("integrations.connection.provider_unreachable", error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Equal(1, connection.Version);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
        Assert.Equal(IntegrationsTestBed.FromNumber, connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
    }

    [Fact]
    public async Task APassingTestBringsNeedsAttentionBackToActive()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);

        var response = await bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999"), Ct);

        Assert.Equal("Active", response.Status);
    }

    [Fact]
    public async Task AStaleVersionIs412BeforeCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999", version: 7), Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    // P14: sin la clave guardada no hay con qué probar el número nuevo.
    [Fact]
    public async Task AnUnreadableStoredSecretWithoutANewOneIs422OnTheSecretField()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.UpdateHandler().HandleAsync(Update(bed, connection, fromNumber: "573009999999"), Ct));

        Assert.Equal("secrets.apiToken", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task AnUnreadableStoredSecretIsFineWhenANewOneComes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var response = await bed.UpdateHandler().HandleAsync(
            Update(bed, connection, secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "nuevo-token" }),
            Ct);

        Assert.Equal(2, response.Version);
    }

    [Fact]
    public async Task NothingChangedIsANoOp()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.UpdateHandler().HandleAsync(Update(bed, connection), Ct);

        Assert.Equal(1, response.Version);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
        Assert.Empty(bed.Tester.Calls);
    }

    // Un diccionario null (no un valor null) es "sin cambios" para ese grupo, nunca un 422 ni un 500.
    [Fact]
    public async Task NullFieldsAndSecretsDictionariesChangeNothingButTheName()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.UpdateHandler().HandleAsync(
            new UpdateConnectionCommand(bed.TenantId, connection.Id, connection.Version, "Sede sur", null, null), Ct);

        Assert.Equal("Sede sur", response.Name);
        Assert.Equal(IntegrationsTestBed.FromNumber, response.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
        Assert.Empty(bed.Tester.Calls);
        Assert.Equal(["name"], Assert.Single(bed.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task NullSecretsDictionaryKeepsTheStoredSecretWhileFieldsChange()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await bed.UpdateHandler().HandleAsync(
            new UpdateConnectionCommand(
                bed.TenantId,
                connection.Id,
                connection.Version,
                "WhatsApp sede norte",
                new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = "573009999999" },
                null),
            Ct);

        Assert.Equal(IntegrationsTestBed.Token, Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal(IntegrationsTestBed.Token, StoredToken(connection));
    }

    [Fact]
    public async Task AnIdOfAnotherTenantIsNotFound()
    {
        var bed = new IntegrationsTestBed();
        var foreign = bed.Seed(tenantId: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.UpdateHandler().HandleAsync(Update(bed, foreign), Ct));

        Assert.Equal("integrations.connection.not_found", error.Code);
    }

    // Review Focus 2, en el PUT.
    [Fact]
    public async Task ASecretSentAsAFieldIsRejectedBeforeAnything()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        var command = Update(bed, connection) with
        {
            Fields = new Dictionary<string, string?>
            {
                [ZenviaFieldKeys.FromNumber] = IntegrationsTestBed.FromNumber,
                [ZenviaFieldKeys.ApiToken] = "en-claro",
            },
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.UpdateHandler().HandleAsync(command, Ct));

        Assert.Equal("fields.apiToken", Assert.Single(error.Errors).PropertyName);
        Assert.DoesNotContain("en-claro", connection.Fields.Values);
    }

    // P6 del plan (spec 2026-10-09 §6.1): una conexión de Meta sólo cambia el nombre; lo que llenó el
    // backend se conserva tal cual, internos incluidos, y no se llama al proveedor.
    [Fact]
    public async Task AMetaSignupConnectionOnlyChangesItsNameAndKeepsItsFields()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.SeedWhatsAppCloud();
        var before = connection.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var response = await bed.UpdateHandler().HandleAsync(
            new UpdateConnectionCommand(bed.TenantId, connection.Id, connection.Version, "Ventas sur", null, null), Ct);

        Assert.Equal("Ventas sur", response.Name);
        Assert.Equal(before, connection.Fields);
        Assert.Equal("1234567890", response.Fields[WhatsAppCloudFieldKeys.PhoneNumberId]);
        Assert.Empty(bed.Tester.Calls);
        Assert.Equal(["name"], Assert.Single(bed.Audit.Entries).ChangedFields);
    }

    // Spec 2026-10-09 §6.1: ni los campos visibles ni los internos ni el token se escriben por PUT.
    [Fact]
    public async Task AMetaSignupConnectionRejectsFieldsAndSecretsSentByThePut()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.SeedWhatsAppCloud();
        var command = new UpdateConnectionCommand(
            bed.TenantId,
            connection.Id,
            connection.Version,
            "Ventas",
            new Dictionary<string, string?>
            {
                [WhatsAppCloudFieldKeys.DisplayPhoneNumber] = "+57 311 000 0000",
                [WhatsAppCloudFieldKeys.PhoneNumberId] = "111",
            },
            new Dictionary<string, string?> { [WhatsAppCloudFieldKeys.AccessToken] = "otro-token-SENTINEL" });

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.UpdateHandler().HandleAsync(command, Ct));

        Assert.Equal(
            ["fields.displayPhoneNumber", "fields.phoneNumberId", "secrets.accessToken"],
            error.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal));
        // El texto lo lee la persona: dice de dónde sale el dato, no cómo está hecho el sistema.
        Assert.All(error.Errors, failure => Assert.Equal("Este dato viene de Meta; no se edita aquí.", failure.ErrorMessage));
        Assert.Equal("1234567890", connection.Fields[WhatsAppCloudFieldKeys.PhoneNumberId]);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    // Un valor vacío cuenta como "no vino": la pantalla puede reenviar el formulario sin tocarlos.
    [Fact]
    public async Task AMetaSignupConnectionToleratesBlankFields()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.SeedWhatsAppCloud();

        var response = await bed.UpdateHandler().HandleAsync(
            new UpdateConnectionCommand(
                bed.TenantId,
                connection.Id,
                connection.Version,
                "Ventas",
                new Dictionary<string, string?> { [WhatsAppCloudFieldKeys.DisplayPhoneNumber] = "  " },
                new Dictionary<string, string?> { [WhatsAppCloudFieldKeys.AccessToken] = null }),
            Ct);

        Assert.Equal("+57 300 123 4567", response.Fields[WhatsAppCloudFieldKeys.DisplayPhoneNumber]);
        Assert.Equal(1, response.Version);
    }
}
