namespace Modules.Coordinadora.Domain;

/// <summary>Lo que la pantalla de configuración guarda de una vez (spec 2026-11-21 §5.2). <paramref name="DefaultPackage"/> es nulo
/// mientras nadie lo configure.</summary>
public sealed record CoordinadoraSettingsInput(
    string? SenderName,
    string? SenderAddress,
    string? SenderPhone,
    Guid? SenderCityId,
    string? AccountCode,
    string? ProductCode,
    int? ServiceLevel,
    string? Div,
    CoordinadoraPackage? DefaultPackage,
    int PackagingType,
    decimal DeclaredValueMinimum,
    bool CollectOnDelivery,
    string? LabelFormat);

/// <summary>
/// Spec 2026-11-21 §5.2: la configuración de la conexión de Coordinadora, una fila por conexión (PK <see cref="ConnectionId"/>): el
/// remitente (origen del despacho), la cuenta y el producto de Coordinadora, el paquete por defecto y los interruptores. Nace vacía al
/// primer acceso. <b>Toda escritura sube <see cref="Version"/></b>, que es el <c>If-Match</c> del <c>PUT</c> y el token de concurrencia
/// de la fila. Nunca guarda una credencial: esas son de la conexión de Integrations.
/// </summary>
public sealed class CoordinadoraSettings
{
    public const int NameMaxLength = 120;
    public const int AddressMaxLength = 200;
    public const int PhoneMaxLength = 30;
    public const int CodeMaxLength = 20;
    public const int LabelFormatMaxLength = 50;
    public const int ServiceLevelMax = 99;

    /// <summary><c>ubl</c>: <c>0</c> automática, <c>1</c> mercancía (spec §3).</summary>
    public const int PackagingAutomatic = 0;
    public const int PackagingGoods = 1;

    private CoordinadoraSettings()
    {
    }

    /// <summary>PK; la conexión de Integrations es una referencia blanda (otro módulo, sin FK).</summary>
    public Guid ConnectionId { get; private set; }

    public Guid TenantId { get; private set; }

    public string? SenderName { get; private set; }

    public string? SenderAddress { get; private set; }

    public string? SenderPhone { get; private set; }

    /// <summary>La ciudad de origen (Geography), referencia blanda. Viaja a Coordinadora como DIVIPOLA + <c>000</c>.</summary>
    public Guid? SenderCityId { get; private set; }

    /// <summary><c>codigo_cuenta</c> / <c>cuenta</c>.</summary>
    public string? AccountCode { get; private set; }

    /// <summary><c>codigo_producto</c> / <c>producto</c>.</summary>
    public string? ProductCode { get; private set; }

    /// <summary><c>nivel_servicio</c>.</summary>
    public int? ServiceLevel { get; private set; }

    /// <summary><c>div</c>: el acuerdo mercantil; vacío si no hay.</summary>
    public string? Div { get; private set; }

    public int? DefaultHeightCm { get; private set; }

    public int? DefaultWidthCm { get; private set; }

    public int? DefaultLengthCm { get; private set; }

    public decimal? DefaultWeightKg { get; private set; }

    /// <summary><c>ubl</c>. Default <see cref="PackagingAutomatic"/>.</summary>
    public int PackagingType { get; private set; }

    /// <summary>El piso del <c>valor_declarado</c>, en pesos. Default 0.</summary>
    public decimal DeclaredValueMinimum { get; private set; }

    /// <summary>Pedir recaudo cuando el pedido es contraentrega. <b>Falso de fábrica</b> [decisión del diseño]: un recaudo es una
    /// obligación financiera y no hay cuenta de pruebas.</summary>
    public bool CollectOnDelivery { get; private set; }

    /// <summary><c>formato_impresion</c>, opcional.</summary>
    public string? LabelFormat { get; private set; }

    /// <summary>Concurrencia optimista e <c>If-Match</c>. Una configuración nueva vale 0; cada escritura sube uno.</summary>
    public long Version { get; private set; }

    /// <summary>El paquete por defecto, o <c>null</c> si falta alguna de sus cuatro medidas.</summary>
    public CoordinadoraPackage? DefaultPackage =>
        DefaultHeightCm is { } height && DefaultWidthCm is { } width && DefaultLengthCm is { } length && DefaultWeightKg is { } weight
            ? new CoordinadoraPackage(height, width, length, weight, 1)
            : null;

    public static CoordinadoraSettings Create(Guid connectionId, Guid tenantId) =>
        new()
        {
            ConnectionId = connectionId,
            TenantId = tenantId,
            PackagingType = PackagingAutomatic,
            DeclaredValueMinimum = 0m,
            CollectOnDelivery = false,
            Version = 0,
        };

    /// <summary>Reemplaza la configuración entera (el <c>PUT</c> manda todo) y sube la versión. Los largos y los rangos son los de las
    /// columnas: un valor que no cabe es <c>coordinadora.settings.value_invalid</c> (los validadores lo frenan antes con el campo).</summary>
    public void Update(CoordinadoraSettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        SenderName = Text(input.SenderName, NameMaxLength, "senderName");
        SenderAddress = Text(input.SenderAddress, AddressMaxLength, "senderAddress");
        SenderPhone = Text(input.SenderPhone, PhoneMaxLength, "senderPhone");
        SenderCityId = input.SenderCityId;
        AccountCode = Text(input.AccountCode, CodeMaxLength, "accountCode");
        ProductCode = Text(input.ProductCode, CodeMaxLength, "productCode");
        if (input.ServiceLevel is { } level && level is < 0 or > ServiceLevelMax)
        {
            throw Invalid("serviceLevel", $"must be between 0 and {ServiceLevelMax}");
        }

        ServiceLevel = input.ServiceLevel;
        Div = Text(input.Div, CodeMaxLength, "div");
        if (input.DefaultPackage is { } package)
        {
            // El paquete ya viene validado por CoordinadoraPackage.Create; se vuelve a pasar por ahí por si alguien construyó el record a mano.
            var valid = CoordinadoraPackage.Create(package.HeightCm, package.WidthCm, package.LengthCm, package.WeightKg, 1);
            (DefaultHeightCm, DefaultWidthCm, DefaultLengthCm, DefaultWeightKg) = (valid.HeightCm, valid.WidthCm, valid.LengthCm, valid.WeightKg);
        }
        else
        {
            (DefaultHeightCm, DefaultWidthCm, DefaultLengthCm, DefaultWeightKg) = (null, null, null, null);
        }

        if (input.PackagingType is not (PackagingAutomatic or PackagingGoods))
        {
            throw Invalid("packagingType", "must be 0 (automatic) or 1 (goods)");
        }

        PackagingType = input.PackagingType;
        if (input.DeclaredValueMinimum < 0 || input.DeclaredValueMinimum != decimal.Truncate(input.DeclaredValueMinimum))
        {
            throw Invalid("declaredValueMinimum", "must be zero or a positive whole amount in pesos");
        }

        DeclaredValueMinimum = input.DeclaredValueMinimum;
        CollectOnDelivery = input.CollectOnDelivery;
        LabelFormat = Text(input.LabelFormat, LabelFormatMaxLength, "labelFormat");
        Version++;
    }

    /// <summary>Los códigos de lo que falta para generar una guía (spec §5.3): <c>sender_incomplete</c> (algún campo del remitente o de la
    /// cuenta) y <c>package_defaults_missing</c>. Que la ciudad del remitente exista y la atienda Coordinadora lo decide Application, que
    /// es quien ve Geography.</summary>
    public IReadOnlyList<string> MissingForShipping()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(SenderName)
            || string.IsNullOrWhiteSpace(SenderAddress)
            || string.IsNullOrWhiteSpace(SenderPhone)
            || SenderCityId is null
            || string.IsNullOrWhiteSpace(AccountCode)
            || string.IsNullOrWhiteSpace(ProductCode)
            || ServiceLevel is null)
        {
            missing.Add(CoordinadoraSettingsReadiness.SenderIncomplete);
        }

        if (DefaultPackage is null)
        {
            missing.Add(CoordinadoraSettingsReadiness.PackageDefaultsMissing);
        }

        return missing;
    }

    private static string? Text(string? value, int maxLength, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : throw Invalid(field, $"cannot exceed {maxLength} characters");
    }

    private static CoordinadoraDomainException Invalid(string field, string rule) =>
        new(CoordinadoraErrorCodes.SettingsValueInvalid, $"The Coordinadora setting '{field}' {rule}.");
}

/// <summary>Los códigos de <see cref="CoordinadoraSettings.MissingForShipping"/>. Son contrato con la pantalla.</summary>
public static class CoordinadoraSettingsReadiness
{
    public const string SenderIncomplete = "sender_incomplete";
    public const string PackageDefaultsMissing = "package_defaults_missing";
}
