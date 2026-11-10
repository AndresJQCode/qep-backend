using System.Reflection;
using Modules.Store.Api;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Store.Infrastructure;

namespace ArchitectureTests;

public sealed class StoreLayerTests
{
    [Fact]
    public void DomainDoesNotReferenceOuterLayers()
    {
        AssertDoesNotReference(
            typeof(OnlineStore).Assembly,
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly);
    }

    [Fact]
    public void ApplicationDoesNotReferenceInfrastructureOrApi()
    {
        AssertDoesNotReference(
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceApi()
    {
        AssertDoesNotReference(
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly);
    }

    // Traducir un error de base (p. ej. la unicidad del dominio o del número de solicitud) es tarea de Infrastructure.
    [Fact]
    public void ApplicationDoesNotReferencePersistenceLibraries()
    {
        var references = ReferenceNamesOf(typeof(StorePermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
             name.StartsWith("Npgsql", StringComparison.Ordinal)));
    }

    /// <summary>
    /// `store` no se acopla a ningún otro módulo de negocio. La única dependencia legítima hacia afuera es
    /// `Modules.Tenancy.Application` (<c>IExecutionContext</c>, <c>ITenantModules</c>). Clientes, geografía y cotizaciones entran por
    /// los puertos <c>IStore*</c> declarados acá, cuyos adaptadores viven en `Bootstrapper`. Sin esta aserción esa decisión es un comentario.
    /// </summary>
    [Fact]
    public void ApplicationOnlyReferencesTenancyAmongTheBusinessModules()
    {
        var references = ReferenceNamesOf(typeof(StorePermissions).Assembly);

        Assert.DoesNotContain(references, name =>
            name is not null &&
            name.StartsWith("Modules.", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Store", StringComparison.Ordinal) &&
            !name.StartsWith("Modules.Tenancy", StringComparison.Ordinal));
    }

    /// <summary>Lo mismo, nombrado: ninguna capa de `store` referencia Customers, Geography ni Quotations.</summary>
    [Fact]
    public void NoStoreLayerReferencesCustomersGeographyOrQuotations()
    {
        Assembly[] layers =
        [
            typeof(OnlineStore).Assembly,
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly,
        ];

        foreach (var layer in layers)
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null &&
                (name.StartsWith("Modules.Customers", StringComparison.Ordinal) ||
                 name.StartsWith("Modules.Geography", StringComparison.Ordinal) ||
                 name.StartsWith("Modules.Quotations", StringComparison.Ordinal)));
        }
    }

    /// <summary>Los compradores dependen de Store, nunca al revés: ninguna capa de `store` referencia `Modules.Buyers*`.</summary>
    [Fact]
    public void NoStoreLayerReferencesBuyers()
    {
        Assembly[] layers =
        [
            typeof(OnlineStore).Assembly,
            typeof(StorePermissions).Assembly,
            typeof(StoreInfrastructureExtensions).Assembly,
            typeof(StoreEndpoints).Assembly,
        ];

        foreach (var layer in layers)
        {
            Assert.DoesNotContain(ReferenceNamesOf(layer), name =>
                name is not null &&
                name.StartsWith("Modules.Buyers", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// `Modules.Store.Application` usa `TenantModuleKeys` de `Modules.Tenancy.Domain`, así que no se puede prohibir la referencia
    /// al assembly. Lo que sí se prohíbe es que un tipo de Application tenga un campo, propiedad, parámetro o retorno del tipo
    /// <c>Tenant</c> o <c>Membership</c>: el dueño entra por el puerto, no por el agregado.
    /// </summary>
    [Fact]
    public void ApplicationDoesNotReferenceTenancyDomainTypesForTheOwner()
    {
        var types = typeof(StorePermissions).Assembly.GetTypes();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var type in types)
        {
            foreach (var field in type.GetFields(all))
                Check(field.FieldType, $"{type.FullName}.{field.Name}");
            foreach (var property in type.GetProperties(all))
                Check(property.PropertyType, $"{type.FullName}.{property.Name}");
            foreach (var constructor in type.GetConstructors(all))
                foreach (var parameter in constructor.GetParameters())
                    Check(parameter.ParameterType, $"{type.FullName}..ctor({parameter.Name})");
            foreach (var method in type.GetMethods(all))
            {
                Check(method.ReturnType, $"{type.FullName}.{method.Name} (retorno)");
                foreach (var parameter in method.GetParameters())
                    Check(parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})");
            }
        }

        Assert.NotEmpty(types);
        Assert.Empty(offenders);

        void Check(Type candidate, string where)
        {
            if (Mentions(candidate)) offenders.Add(where);
        }

        static bool Mentions(Type candidate)
        {
            if (candidate.HasElementType && Mentions(candidate.GetElementType()!)) return true;
            if (candidate.IsGenericType && candidate.GetGenericArguments().Any(Mentions)) return true;
            return candidate.Namespace == "Modules.Tenancy.Domain" && candidate.Name is "Tenant" or "Membership";
        }
    }

    private static string?[] ReferenceNamesOf(Assembly assembly) => assembly
        .GetReferencedAssemblies()
        .Select(name => name.Name)
        .ToArray();

    private static void AssertDoesNotReference(
        Assembly source,
        params Assembly[] forbiddenAssemblies)
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
