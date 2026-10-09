namespace Modules.Integrations.Domain;

public enum ConnectionStatus
{
    Active,
    Paused,
    NeedsAttention,
}

/// <summary>
/// Cifra un secreto para una conexión y un campo (AAD, D7). Lo cumple <c>ISecretProtector.Protect</c>:
/// el dominio sella sin ver la llave ni guardar el texto plano.
/// </summary>
public delegate ProtectedSecret SecretSealer(Guid connectionId, string fieldKey, string plaintext);

/// <summary>Una fila de <c>integrations.connection_secrets</c> (D9).</summary>
public sealed class ConnectionSecret
{
    // EF lee y escribe los bytes por este campo: un byte[] público invitaría a mutarlo por afuera.
    private byte[] _ciphertext = [];

    private ConnectionSecret()
    {
    }

    internal ConnectionSecret(string fieldKey, ProtectedSecret secret, DateTimeOffset updatedAt)
    {
        FieldKey = fieldKey;
        KeyId = secret.KeyId;
        _ciphertext = secret.Ciphertext;
        UpdatedAt = updatedAt;
    }

    public string FieldKey { get; private set; } = string.Empty;

    public string KeyId { get; private set; } = string.Empty;

    /// <summary>Cuándo se guardó este valor («Configurada el …»). Re-cifrarlo no lo cambia.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public ProtectedSecret Protected => new(KeyId, _ciphertext);

    internal void Replace(ProtectedSecret secret, DateTimeOffset updatedAt)
    {
        Reprotect(secret);
        UpdatedAt = updatedAt;
    }

    internal void Reprotect(ProtectedSecret secret)
    {
        KeyId = secret.KeyId;
        _ciphertext = secret.Ciphertext;
    }

    public override string ToString() => $"ConnectionSecret {{ FieldKey = {FieldKey}, KeyId = {KeyId} }}";
}

/// <summary>
/// Una conexión del tenant con un proveedor (spec 2026-10-08, «Conexión»). Valida todo antes de
/// asignar nada, guarda los campos públicos en <see cref="Fields"/> y los secretos sellados en
/// <see cref="Secrets"/>. Nunca devuelve ni registra un valor: <c>changedFields</c> va por clave.
/// </summary>
public sealed class IntegrationConnection
{
    public const int NameMaxLength = 80;
    public const int FailureCodeMaxLength = 64;

    private const string NameKey = "name";

    private readonly List<ConnectionSecret> _secrets = [];

    private IntegrationConnection()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string ProviderKey { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public ConnectionStatus Status { get; private set; }

    public IReadOnlyDictionary<string, string> Fields { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<ConnectionSecret> Secrets => _secrets;

    public DateTimeOffset? LastVerifiedAt { get; private set; }

    public DateTimeOffset? LastFailureAt { get; private set; }

    public string? LastFailureCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>MemberId de quien la creó.</summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>
    /// Una conexión <c>Active</c> y ya verificada: sólo se llama después de una prueba exitosa contra
    /// el proveedor (decisión 5). Genera el id antes de sellar porque el AAD lo lleva (D7).
    /// </summary>
    public static IntegrationConnection Create(
        IntegrationProvider provider,
        Guid tenantId,
        string name,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        SecretSealer seal,
        Guid createdBy,
        DateTimeOffset now)
    {
        var normalizedName = NormalizeName(name);
        var normalizedFields = Normalize(provider, fields, secret: false, requireAll: true);
        var normalizedSecrets = Normalize(provider, secrets, secret: true, requireAll: true);

        var connection = new IntegrationConnection
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProviderKey = provider.Key,
            Name = normalizedName,
            Status = ConnectionStatus.Active,
            Fields = normalizedFields,
            LastVerifiedAt = now,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
            Version = 1,
        };

        foreach (var (key, plaintext) in normalizedSecrets)
        {
            connection._secrets.Add(new ConnectionSecret(key, seal(connection.Id, key, plaintext), now));
        }

        return connection;
    }

    /// <summary>
    /// Reemplaza nombre y campos públicos; un secreto ausente conserva el guardado (D5). Con
    /// <paramref name="verified"/> la prueba ya pasó: anota la verificación y saca de
    /// <c>NeedsAttention</c>. Sin cambios y sin verificación es un no-op: no hay versión nueva.
    /// </summary>
    /// <returns>Las claves que cambiaron (<c>name</c>, campos y secretos que llegaron), ordenadas.</returns>
    public IReadOnlyList<string> Update(
        IntegrationProvider provider,
        string name,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        SecretSealer seal,
        DateTimeOffset now,
        bool verified)
    {
        if (!string.Equals(provider.Key, ProviderKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The provider does not match the connection.", nameof(provider));
        }

        var normalizedName = NormalizeName(name);
        var normalizedFields = Normalize(provider, fields, secret: false, requireAll: true);
        var replacedSecrets = Normalize(provider, secrets, secret: true, requireAll: false);

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.Equals(normalizedName, Name, StringComparison.Ordinal))
        {
            changed.Add(NameKey);
        }

        foreach (var key in normalizedFields.Keys.Union(Fields.Keys, StringComparer.Ordinal))
        {
            if (!normalizedFields.TryGetValue(key, out var next)
                || !Fields.TryGetValue(key, out var current)
                || !string.Equals(next, current, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }

        // P8: un secreto que llegó cuenta como cambio; no se descifra el guardado para comparar.
        changed.UnionWith(replacedSecrets.Keys);

        if (changed.Count == 0 && !verified)
        {
            return [];
        }

        Name = normalizedName;
        Fields = normalizedFields;
        foreach (var (key, plaintext) in replacedSecrets)
        {
            var protectedSecret = seal(Id, key, plaintext);
            var existing = FindSecret(key);
            if (existing is null)
            {
                _secrets.Add(new ConnectionSecret(key, protectedSecret, now));
            }
            else
            {
                existing.Replace(protectedSecret, now);
            }
        }

        if (verified)
        {
            ApplyVerification(now);
        }

        Touch(now);
        return changed.ToArray();
    }

    public void Pause(DateTimeOffset now)
    {
        if (Status != ConnectionStatus.Active)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NotActive, "Only an active connection can be paused.");
        }

        Status = ConnectionStatus.Paused;
        Touch(now);
    }

    /// <summary>Lo que <c>resume</c> exige antes de llamar al proveedor (D4).</summary>
    public void EnsurePaused()
    {
        if (Status != ConnectionStatus.Paused)
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NotPaused, "Only a paused connection can be resumed.");
        }
    }

    /// <summary>Después de una prueba exitosa (D4).</summary>
    public void Resume(DateTimeOffset now)
    {
        EnsurePaused();
        Status = ConnectionStatus.Active;
        ApplyVerification(now);
        Touch(now);
    }

    public void MarkVerified(DateTimeOffset now)
    {
        ApplyVerification(now);
        Touch(now);
    }

    public void MarkNeedsAttention(string failureCode, DateTimeOffset now)
    {
        RegisterFailure(failureCode, now);
        Status = ConnectionStatus.NeedsAttention;
    }

    /// <summary>Anota una prueba fallida sin cambiar el estado (P10).</summary>
    public void RecordFailure(string failureCode, DateTimeOffset now) => RegisterFailure(failureCode, now);

    /// <summary>
    /// El mismo valor, cifrado con otra llave (el worker de rotación). Sube la versión para que un
    /// <c>PUT</c> en el medio choque (P16); no cambia la fecha en que se configuró el secreto.
    /// </summary>
    public void Reprotect(string fieldKey, ProtectedSecret secret, DateTimeOffset now)
    {
        var existing = FindSecret(fieldKey)
            ?? throw new InvalidOperationException($"The connection has no secret '{fieldKey}' to re-encrypt.");
        existing.Reprotect(secret);
        Touch(now);
    }

    private ConnectionSecret? FindSecret(string fieldKey) =>
        _secrets.Find(stored => string.Equals(stored.FieldKey, fieldKey, StringComparison.Ordinal));

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    // Una prueba exitosa saca de NeedsAttention; Paused sólo lo cambia Resume.
    private void ApplyVerification(DateTimeOffset now)
    {
        LastVerifiedAt = now;
        if (Status == ConnectionStatus.NeedsAttention)
        {
            Status = ConnectionStatus.Active;
        }
    }

    // Valida el código antes de asignar nada; lo comparten MarkNeedsAttention y RecordFailure.
    private void RegisterFailure(string failureCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        if (failureCode.Length > FailureCodeMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode), $"A failure code is at most {FailureCodeMaxLength} characters.");
        }

        LastFailureAt = now;
        LastFailureCode = failureCode;
        Touch(now);
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength || trimmed.Any(char.IsControl))
        {
            throw new IntegrationsDomainException(
                IntegrationsErrorCodes.NameInvalid,
                $"A connection name is 1 to {NameMaxLength} characters on a single line.");
        }

        return trimmed;
    }

    private static Dictionary<string, string> Normalize(
        IntegrationProvider provider, IReadOnlyDictionary<string, string> values, bool secret, bool requireAll)
    {
        foreach (var key in values.Keys)
        {
            if (provider.FindField(key) is not { } known || known.IsSecret != secret)
            {
                throw new IntegrationsDomainException(
                    IntegrationsErrorCodes.FieldUnknown,
                    secret
                        ? "A secret was sent that the provider does not declare as secret."
                        : "A field was sent that the provider does not declare as public.");
            }
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in secret ? provider.SecretFields : provider.PublicFields)
        {
            var value = values.TryGetValue(definition.Key, out var raw) ? raw.Trim() : string.Empty;
            if (value.Length == 0)
            {
                if (requireAll && definition.Required)
                {
                    throw new IntegrationsDomainException(
                        IntegrationsErrorCodes.FieldRequired, $"The field '{definition.Key}' is required.");
                }

                continue;
            }

            if (definition.IsTooLong(value) || !definition.HasValidShape(value))
            {
                throw new IntegrationsDomainException(
                    IntegrationsErrorCodes.FieldInvalid, $"The field '{definition.Key}' does not have a valid value.");
            }

            normalized[definition.Key] = value;
        }

        return normalized;
    }
}
