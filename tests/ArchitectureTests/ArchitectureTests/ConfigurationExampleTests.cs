using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace ArchitectureTests;

/// <summary>
/// <c>appsettings.example.json</c> no lo lee nadie en runtime: es la única documentación de qué
/// claves existen. Por eso se desincroniza sin que nada se ponga rojo — una propiedad nueva en
/// una clase de options compila, arranca y funciona con su valor por defecto, y el archivo que
/// el operador lee para configurar el ambiente sigue sin mencionarla.
///
/// El costo no es cosmético. <c>Seed:Enabled</c> y <c>Seed:OwnerEmail</c> conceden el rol admin
/// de un tenant a un email, están activas en <c>k8s/prod-configMap.yaml</c> y nunca llegaron al
/// ejemplo: para enterarse de que ese mecanismo existe hay que abrir el ConfigMap de producción.
///
/// Advertirlo en un documento ya se demostró insuficiente. Esta prueba lo convierte en regla.
///
/// Alcance: cubre las claves que bindea una clase de options (las que declaran
/// <c>SectionName</c>). Las que se leen sueltas con <c>configuration["..."]</c>
/// —<c>ConnectionStrings:QepDatabase</c>, la sección <c>Authentication</c> fuera de
/// <c>Session</c>, <c>Registration:PublicTenantSignupEnabled</c>, <c>OpenTelemetry:Endpoint</c>—
/// quedan afuera: atraparlas exigiría una lista escrita a mano, que es exactamente lo que se
/// pudre.
/// </summary>
public sealed class ConfigurationExampleTests
{
    private const string ExampleRelativePath = "src/Api/appsettings.example.json";

    [Fact]
    public void EveryBoundConfigurationKeyIsDocumentedInTheExample()
    {
        var example = ExampleDocument();

        var missing = OptionsRoots()
            .SelectMany(KeysOf)
            .Where(key => !Exists(example.RootElement, key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Estas claves las bindea una clase de options y no están en {ExampleRelativePath}, "
                + "así que quien configure el ambiente leyendo ese archivo no se entera de que "
                + "existen: "
                + string.Join(", ", missing));
    }

    /// <summary>
    /// Sin tipos ni claves descubiertos, la prueba de arriba pasaría por vacía y no verificaría
    /// nada. Esto ancla que la reflexión sigue viendo los ensamblados.
    /// </summary>
    [Fact]
    public void OptionsDiscoveryFindsTheBoundSections()
    {
        Assert.NotEmpty(OptionsRoots());
        Assert.NotEmpty(OptionsRoots().SelectMany(KeysOf).ToArray());
    }

    // Se descubren por patrón de archivo y no por una lista de tipos ancla, mismo criterio que
    // CompositionRootTests: una lista hay que acordarse de ampliarla al abrir un módulo nuevo, y
    // ese olvido es justo lo que esta prueba existe para atrapar.
    private static Type[] OptionsRoots() =>
        Directory
            .GetFiles(AppContext.BaseDirectory, "Modules.*.Infrastructure.dll")
            .Append(Path.Combine(AppContext.BaseDirectory, "Bootstrapper.dll"))
            .Where(File.Exists)
            .Select(Assembly.LoadFrom)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => SectionNameOf(type) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    // La marca de una clase de options es su constante SectionName; es lo que se le pasa a
    // GetSection, así que es la misma fuente de verdad que usa el binder.
    private static string? SectionNameOf(Type type) =>
        type.GetField("SectionName", BindingFlags.Public | BindingFlags.Static) is
            { IsLiteral: true } field
        && field.FieldType == typeof(string)
            ? (string?)field.GetRawConstantValue()
            : null;

    private static IEnumerable<string> KeysOf(Type root) => Keys(root, SectionNameOf(root)!);

    private static IEnumerable<string> Keys(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var path = $"{prefix}:{property.Name}";
            if (IsSection(property.PropertyType))
            {
                foreach (var nested in Keys(property.PropertyType, path))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    // Una propiedad cuyo tipo es una clase propia es una subsección (Storage:R2,
    // Notifications:Infobip, Quotations:WhatsApp); todo lo demás —string, número, bool, enum— es
    // una hoja, y es la hoja la que tiene que aparecer en el ejemplo.
    private static bool IsSection(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type);

    private static bool Exists(JsonElement element, string key)
    {
        foreach (var segment in key.Split(':'))
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoringCase(element, segment, out element))
            {
                return false;
            }
        }

        return true;
    }

    // El binder de configuración no distingue mayúsculas de minúsculas; la prueba tampoco, o
    // marcaría como faltante una clave que la app sí resuelve.
    private static bool TryGetPropertyIgnoringCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // File.ReadAllText descarta el BOM, que el archivo tiene y Utf8JsonReader no saltea solo.
    // Los comentarios se permiten porque el proveedor JSON de configuración también los acepta.
    private static JsonDocument ExampleDocument() =>
        JsonDocument.Parse(
            File.ReadAllText(ExampleFilePath()),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

    // Se busca hacia arriba desde el directorio de salida: la prueba no puede depender de
    // cuántos niveles hay entre bin/ y la raíz del repositorio.
    private static string ExampleFilePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ExampleRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"No se encontró {ExampleRelativePath} subiendo desde {AppContext.BaseDirectory}.");
    }
}
