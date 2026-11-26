using System.Reflection;
using Modules.Wms.Api;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Infrastructure;

namespace ArchitectureTests;

/// <summary>
/// Capas del módulo Wms (spec WMS 2026-10-31 §4.1 y §11.1), copia de <see cref="PipelineLayerTests"/>. Wms nunca referencia
/// Catalog, Geography, Quotations, Suppliers, Storage ni Identity: lo que necesita de ellos entra por puertos propios con
/// adaptadores en Bootstrapper. La única excepción es Infrastructure hacia Audit, para la proyección de <c>audit.entries</c>.
/// </summary>
public sealed class WmsLayerTests
{
    private static readonly Assembly Domain = typeof(WmsErrorCodes).Assembly;
    private static readonly Assembly Application = typeof(WmsPermissions).Assembly;
    private static readonly Assembly Infrastructure = typeof(WmsInfrastructureExtensions).Assembly;
    private static readonly Assembly Api = typeof(WmsEndpoints).Assembly;

    [Fact]
    public void DomainDoesNotReferenceOuterLayers() =>
        AssertDoesNotReference(Domain, Application, Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi() =>
        AssertDoesNotReference(Application, Infrastructure, Api);

    [Fact]
    public void InfrastructureDoesNotReferenceApi() =>
        AssertDoesNotReference(Infrastructure, Api);

    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));

    // Tenancy (ITenantModules, TenantModuleGuard, IExecutionContext) y nada más.
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheModules() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Wms", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));

    [Fact]
    public void DomainReferencesNoOtherModule() =>
        Assert.DoesNotContain(ReferenceNamesOf(Domain), name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Wms", StringComparison.Ordinal));

    // Ni siquiera Infrastructure: los adaptadores viven en Bootstrapper.
    [Theory]
    [InlineData("Modules.Catalog")]
    [InlineData("Modules.Geography")]
    [InlineData("Modules.Quotations")]
    [InlineData("Modules.Suppliers")]
    [InlineData("Modules.Storage")]
    [InlineData("Modules.Identity")]
    public void NoLayerReferencesTheModulesBehindThePorts(string forbiddenPrefix)
    {
        foreach (var assembly in new[] { Domain, Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(ReferenceNamesOf(assembly), name =>
                name is not null && name.StartsWith(forbiddenPrefix, StringComparison.Ordinal));
        }
    }

    // La auditoría atómica es de Infrastructure (IWmsAuditRecorder); Application la usa sólo por el puerto.
    [Fact]
    public void ApplicationDoesNotReferenceAudit() =>
        Assert.DoesNotContain(ReferenceNamesOf(Application), name =>
            name is not null && name.StartsWith("Modules.Audit", StringComparison.Ordinal));

    // El ledger es el único que escribe saldos y movimientos (RNF-12): si el contexto expusiera un DbSet de esas tablas, cualquier
    // repositorio podría hacer UPDATE/INSERT por EF y saltarse el upsert guardado, la netea de claves y el orden de bloqueos.
    [Fact]
    public void TheDbContextExposesNoSetForLedgerTables()
    {
        var sets = typeof(Modules.Wms.Infrastructure.Persistence.WmsDbContext)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => property.PropertyType)
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .Select(type => type.GetGenericArguments()[0])
            .ToArray();

        Assert.DoesNotContain(typeof(Modules.Wms.Domain.Inventory.StockMovement), sets);
        Assert.DoesNotContain(typeof(Modules.Wms.Domain.Inventory.StockBalance), sets);
    }

    // StockMovement.Record es internal y sólo lo ven Infrastructure y las dos pruebas: Application no puede construir un movimiento,
    // así que nada fuera del ledger llega a escribir una fila.
    [Fact]
    public void OnlyTheLedgerConstructsMovements()
    {
        var record = typeof(Modules.Wms.Domain.Inventory.StockMovement)
            .GetMethod("Record", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(record);
        Assert.True(record.IsAssembly);

        var visibleTo = Domain
            .GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .ToArray();

        Assert.DoesNotContain(Application.GetName().Name, visibleTo);
        Assert.Contains(Infrastructure.GetName().Name, visibleTo);
        Assert.Equal(
            ["Modules.Wms.Infrastructure", "Modules.Wms.IntegrationTests", "Modules.Wms.UnitTests"],
            visibleTo.Order(StringComparer.Ordinal));
    }

    // §4.1: la regla interna del Domain. Mira tipos de campos, propiedades, parámetros, retornos, bases e interfaces
    // (no cuerpos de método): es lo que decide si un namespace «conoce» a otro.
    [Theory]
    [InlineData("Inventory", new string[] { })]
    [InlineData("Warehousing", new[] { "Inventory" })]
    [InlineData("Inbound", new[] { "Inventory", "Warehousing" })]
    [InlineData("Internal", new[] { "Inventory", "Warehousing" })]
    [InlineData("Control", new[] { "Inventory", "Warehousing" })]
    [InlineData("Outbound", new[] { "Inventory", "Warehousing" })]
    [InlineData("Tasks", new[] { "Inventory", "Warehousing" })]
    public void DomainAreasOnlyDependDownwards(string area, string[] allowed)
    {
        var areas = new[] { "Inventory", "Warehousing", "Inbound", "Internal", "Control", "Outbound", "Tasks" };
        var forbidden = areas
            .Where(other => other != area && !allowed.Contains(other))
            .Select(other => $"Modules.Wms.Domain.{other}")
            .ToArray();

        foreach (var type in Domain.GetTypes().Where(type => type.Namespace == $"Modules.Wms.Domain.{area}"))
        {
            Assert.DoesNotContain(ReferencedTypesOf(type), referenced => forbidden.Contains(referenced.Namespace));
        }
    }

    // Spec 2026-11-26 §4: el motor de tareas es un área al nivel de las de proceso. Sin tipos, la fila de arriba pasaría sin probar nada.
    [Fact]
    public void TheTasksAreaHasTypes() =>
        Assert.Contains(Domain.GetTypes(), type => type.Namespace == "Modules.Wms.Domain.Tasks");

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static HashSet<Type> ReferencedTypesOf(Type type)
    {
        var found = new HashSet<Type>();

        void Add(Type? candidate)
        {
            if (candidate is null)
            {
                return;
            }

            if (candidate.HasElementType)
            {
                Add(candidate.GetElementType());
            }

            if (candidate.IsGenericType)
            {
                foreach (var argument in candidate.GetGenericArguments())
                {
                    Add(argument);
                }
            }

            found.Add(candidate);
        }

        Add(type.BaseType);
        foreach (var contract in type.GetInterfaces())
        {
            Add(contract);
        }

        foreach (var field in type.GetFields(AllDeclared))
        {
            Add(field.FieldType);
        }

        foreach (var property in type.GetProperties(AllDeclared))
        {
            Add(property.PropertyType);
        }

        foreach (var method in type.GetMethods(AllDeclared))
        {
            Add(method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                Add(parameter.ParameterType);
            }
        }

        foreach (var constructor in type.GetConstructors(AllDeclared))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                Add(parameter.ParameterType);
            }
        }

        return found;
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(Assembly source, params Assembly[] forbiddenAssemblies)
    {
        var references = source
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var forbidden in forbiddenAssemblies)
        {
            Assert.DoesNotContain(forbidden.GetName().Name, references);
        }
    }
}
