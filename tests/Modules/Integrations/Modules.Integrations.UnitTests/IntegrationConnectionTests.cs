using Modules.Integrations.Domain;
using static Modules.Integrations.UnitTests.ConnectionFixtures;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Reglas del agregado» y «Estados y transiciones»: validar todo antes de asignar,
/// un secreto ausente conserva el guardado, <c>changedFields</c> por clave y nunca por valor, y las
/// transiciones con sus dos códigos.
/// </summary>
public sealed class IntegrationConnectionTests
{
    private static readonly IntegrationProvider OtherProvider = new(
        "otro", "Otro", IntegrationCategory.Messaging, [Modules.Tenancy.Domain.TenantModuleKeys.Quotations],
        [new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 64, pattern: null, invalidMessage: "Revisa la clave.")],
        maxConnections: 1);

    private static IntegrationsDomainException Rejects(Action action) =>
        Assert.Throws<IntegrationsDomainException>(action);

    private static IntegrationConnection CreateWith(
        Dictionary<string, string> fields, Dictionary<string, string> secrets, RecordingSealer? sealer = null) =>
        IntegrationConnection.Create(
            IntegrationProviders.Zenvia, TenantId, "Norte", fields, secrets, (sealer ?? new RecordingSealer()).Seal, MemberId, Now);

    [Fact]
    public void CreateIsActiveVerifiedAtVersionOneAndKeepsOnlyPublicFieldsInFields()
    {
        var connection = Create(new RecordingSealer());

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Now, connection.LastVerifiedAt);
        Assert.Equal(1, connection.Version);
        Assert.Equal("zenvia", connection.ProviderKey);
        Assert.Equal(TenantId, connection.TenantId);
        Assert.Equal(MemberId, connection.CreatedBy);
        Assert.Equal(Now, connection.CreatedAt);
        Assert.Equal(Now, connection.UpdatedAt);
        Assert.Equal(["fromNumber"], connection.Fields.Keys);
        Assert.Equal(FromNumber, connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Null(connection.LastFailureCode);
        var secret = Assert.Single(connection.Secrets);
        Assert.Equal(ZenviaFieldKeys.ApiToken, secret.FieldKey);
        Assert.Equal("k1", secret.KeyId);
        Assert.Equal(Now, secret.UpdatedAt);
    }

    // D7: el AAD lleva la conexión y el campo, así que el id existe antes de sellar.
    [Fact]
    public void CreateSealsEachSecretWithItsOwnConnectionAndField()
    {
        var sealer = new RecordingSealer();

        var connection = Create(sealer);

        var call = Assert.Single(sealer.Calls);
        Assert.Equal((connection.Id, ZenviaFieldKeys.ApiToken, Token), call);
        Assert.NotEqual(Guid.Empty, connection.Id);
    }

    [Fact]
    public void CreateTrimsTheNameAndTheValues()
    {
        var sealer = new RecordingSealer();

        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia, TenantId, "  Sede norte  ", Fields("  573001234567 "), Secrets(" tok-1 "),
            sealer.Seal, MemberId, Now);

        Assert.Equal("Sede norte", connection.Name);
        Assert.Equal("573001234567", connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal("tok-1", Assert.Single(sealer.Calls).Plaintext);
    }

    // Review Focus 1: \0 y saltos de línea no llegan a la base.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\u0000b")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void CreateRejectsAnInvalidNameWithoutSealingAnything(string name)
    {
        var sealer = new RecordingSealer();

        var error = Rejects(() => Create(sealer, name));

        Assert.Equal(IntegrationsErrorCodes.NameInvalid, error.Code);
        Assert.Empty(sealer.Calls);
    }

    [Fact]
    public void CreateRejectsAMissingOrBlankRequiredField()
    {
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(None(), Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(Fields("   "), Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(Fields(), None())).Code);
    }

    // Review Focus 2: un secreto mandado como campo público terminaría en jsonb, en claro.
    [Fact]
    public void CreateRejectsASecretSentAsAPublicFieldAndAnyUnknownKey()
    {
        var withSecretInFields = Fields();
        withSecretInFields[ZenviaFieldKeys.ApiToken] = Token;
        var withUnknownField = Fields();
        withUnknownField["templateId"] = "x";
        var withPublicInSecrets = Secrets();
        withPublicInSecrets[ZenviaFieldKeys.FromNumber] = FromNumber;

        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => CreateWith(withSecretInFields, Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => CreateWith(withUnknownField, Secrets())).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => CreateWith(Fields(), withPublicInSecrets)).Code);
    }

    [Theory]
    [InlineData("+573001234567")]
    [InlineData("57300123")]
    [InlineData("5730012345678901")]
    [InlineData("57300\u00001234567")]
    public void CreateRejectsAFieldWithoutItsShapeOrTooLong(string fromNumber)
    {
        var sealer = new RecordingSealer();

        var error = Rejects(() => CreateWith(Fields(fromNumber), Secrets(), sealer));

        Assert.Equal(IntegrationsErrorCodes.FieldInvalid, error.Code);
        Assert.DoesNotContain(fromNumber, error.Message, StringComparison.Ordinal);
        Assert.Empty(sealer.Calls);
    }

    // D5: un secreto ausente conserva el guardado.
    [Fact]
    public void UpdateWithAnAbsentSecretKeepsTheStoredOneAndReportsOnlyWhatChanged()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);
        var stored = connection.Secrets[0].Protected;

        var changed = connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields("573009999999"), None(), sealer.Seal, Later, verified: false);

        Assert.Equal(["fromNumber"], changed);
        Assert.Equal("573009999999", connection.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(stored.Ciphertext, connection.Secrets[0].Protected.Ciphertext);
        Assert.Equal(Now, connection.Secrets[0].UpdatedAt);
        Assert.Equal(2, connection.Version);
        Assert.Equal(Later, connection.UpdatedAt);
    }

    [Fact]
    public void UpdateWithANewSecretReplacesItAndReportsItsKeyNeverItsValue()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        var changed = connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields(), Secrets("nuevo-token"), sealer.Seal, Later, verified: true);

        Assert.Equal(["apiToken"], changed);
        Assert.DoesNotContain("nuevo-token", changed);
        Assert.EndsWith("|nuevo-token", RecordingSealer.Open(connection.Secrets[0].Protected), StringComparison.Ordinal);
        Assert.Equal(Later, connection.Secrets[0].UpdatedAt);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    [Fact]
    public void UpdateRenamingReportsTheNameKey()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        Assert.Equal(["name"], connection.Update(
            IntegrationProviders.Zenvia, "Sede sur", Fields(), None(), sealer.Seal, Later, verified: false));
        Assert.Equal("Sede sur", connection.Name);
    }

    // Sin cambios no hay versión nueva: si no, quien tiene la pantalla abierta recibiría un 412 falso.
    [Fact]
    public void UpdateWithoutChangesIsANoOp()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        var changed = connection.Update(
            IntegrationProviders.Zenvia, " WhatsApp sede norte ", Fields(), None(), sealer.Seal, Later, verified: false);

        Assert.Empty(changed);
        Assert.Equal(1, connection.Version);
        Assert.Equal(Now, connection.UpdatedAt);
    }

    [Fact]
    public void AVerifiedUpdateBringsNeedsAttentionBackToActive()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);
        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Now);

        connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields("573009999999"), None(), sealer.Seal, Later, verified: true);

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    [Fact]
    public void UpdateWithAnotherProviderIsAProgrammingError()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);

        Assert.Throws<ArgumentException>(() => connection.Update(
            OtherProvider, "Norte", None(), None(), sealer.Seal, Later, verified: false));
    }

    [Fact]
    public void PauseMovesActiveToPausedAndOnlyFromActive()
    {
        var connection = Create(new RecordingSealer());

        connection.Pause(Later);

        Assert.Equal(ConnectionStatus.Paused, connection.Status);
        Assert.Equal(2, connection.Version);
        Assert.Equal(IntegrationsErrorCodes.NotActive, Rejects(() => connection.Pause(Later)).Code);

        var attention = Create(new RecordingSealer());
        attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Now);
        Assert.Equal(IntegrationsErrorCodes.NotActive, Rejects(() => attention.Pause(Later)).Code);
    }

    [Fact]
    public void ResumeOnlyFromPausedAndMarksItVerified()
    {
        var connection = Create(new RecordingSealer());

        Assert.Equal(IntegrationsErrorCodes.NotPaused, Rejects(() => connection.Resume(Later)).Code);
        Assert.Equal(IntegrationsErrorCodes.NotPaused, Rejects(connection.EnsurePaused).Code);

        connection.Pause(Now);
        connection.EnsurePaused();
        connection.Resume(Later);

        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    [Fact]
    public void MarkNeedsAttentionRecordsTheFailure()
    {
        var connection = Create(new RecordingSealer());

        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Later);

        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal(Later, connection.LastFailureAt);
        Assert.Equal("credentials_rejected", connection.LastFailureCode);
        Assert.Equal(2, connection.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void AFailureCodeMustFitItsColumn(string code)
    {
        var connection = Create(new RecordingSealer());

        Assert.ThrowsAny<ArgumentException>(() => connection.MarkNeedsAttention(code, Later));
        Assert.ThrowsAny<ArgumentException>(() => connection.RecordFailure(code, Later));
    }

    [Fact]
    public void MarkVerifiedBringsNeedsAttentionBackAndRecordFailureKeepsTheStatus()
    {
        var connection = Create(new RecordingSealer());

        connection.RecordFailure(ConnectionFailureCodes.ProviderUnreachable, Now);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal("provider_unreachable", connection.LastFailureCode);

        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, Now);
        connection.MarkVerified(Later);
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(Later, connection.LastVerifiedAt);
    }

    // P16: re-cifrar con otra llave sube la versión (un PUT en el medio choca) pero no cambia la fecha
    // en que se configuró el secreto.
    [Fact]
    public void ReprotectChangesTheKeyButNotWhenTheSecretWasSet()
    {
        var connection = Create(new RecordingSealer());
        var rekeyed = new RecordingSealer { KeyId = "k2" }.Seal(connection.Id, ZenviaFieldKeys.ApiToken, Token);

        connection.Reprotect(ZenviaFieldKeys.ApiToken, rekeyed, Later);

        Assert.Equal("k2", connection.Secrets[0].KeyId);
        Assert.Equal(Now, connection.Secrets[0].UpdatedAt);
        Assert.Equal(2, connection.Version);
        Assert.Throws<InvalidOperationException>(() => connection.Reprotect(ZenviaFieldKeys.FromNumber, rekeyed, Later));
    }

    [Fact]
    public void ASealerThatThrowsOnUpdateLeavesTheAggregateUntouched()
    {
        var connection = Create(new RecordingSealer());
        var before = Snapshot(connection);

        Assert.Throws<InvalidOperationException>(() => connection.Update(
            IntegrationProviders.Zenvia, "Sede sur", Fields("573009999999"), Secrets("nuevo"),
            (_, _, _) => throw new InvalidOperationException("sealer down"), Later, verified: true));

        Assert.Equal(before, Snapshot(connection));
    }

    [Fact]
    public void UpdateRejectsWhatCreateRejectsAndLeavesTheAggregateUntouched()
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);
        var before = Snapshot(connection);
        var secretInFields = Fields();
        secretInFields[ZenviaFieldKeys.ApiToken] = Token;

        Assert.Equal(IntegrationsErrorCodes.NameInvalid, Rejects(() => connection.Update(
            IntegrationProviders.Zenvia, "a\nb", Fields(), Secrets("nuevo"), sealer.Seal, Later, verified: true)).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldInvalid, Rejects(() => connection.Update(
            IntegrationProviders.Zenvia, "Sede sur", Fields("57300\u00001234567"), None(), sealer.Seal, Later, verified: true)).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldUnknown, Rejects(() => connection.Update(
            IntegrationProviders.Zenvia, "Sede sur", secretInFields, None(), sealer.Seal, Later, verified: true)).Code);

        Assert.Equal(before, Snapshot(connection));
        Assert.Single(sealer.Calls);
    }

    // Review Focus 3: un null del JSON no es un 500.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateWithANullOrBlankSecretKeepsTheStoredOne(string? token)
    {
        var sealer = new RecordingSealer();
        var connection = Create(sealer);
        var before = Snapshot(connection);

        var changed = connection.Update(
            IntegrationProviders.Zenvia, "WhatsApp sede norte", Fields(), Secrets(token!), sealer.Seal, Later, verified: false);

        Assert.Empty(changed);
        Assert.Equal(before, Snapshot(connection));
        Assert.Equal(1, connection.Version);
    }

    [Fact]
    public void CreateTreatsANullSecretOrPublicFieldAsMissing()
    {
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(Fields(), Secrets(null!))).Code);
        Assert.Equal(IntegrationsErrorCodes.FieldRequired, Rejects(() => CreateWith(Fields(null!), Secrets())).Code);
    }

    private static (string Name, string Fields, long Version, DateTimeOffset UpdatedAt, string Secret) Snapshot(
        IntegrationConnection connection) =>
        (connection.Name,
            string.Join(",", connection.Fields.Select(pair => $"{pair.Key}={pair.Value}")),
            connection.Version,
            connection.UpdatedAt,
            Convert.ToBase64String(connection.Secrets[0].Protected.Ciphertext));

    [Fact]
    public void ASecretPrintsItsKeyButNeverItsBytes() =>
        Assert.Equal(
            "ConnectionSecret { FieldKey = apiToken, KeyId = k1 }",
            Create(new RecordingSealer()).Secrets[0].ToString());
}
