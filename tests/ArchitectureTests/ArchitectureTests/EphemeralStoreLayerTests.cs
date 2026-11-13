using System.Reflection;

namespace ArchitectureTests;

/// <summary>
/// P-1 del spec de Valkey (2026-11-13): <c>StackExchange.Redis</c> lo referencia sólo Bootstrapper. Un módulo
/// que necesite estado compartido declara su puerto y el adaptador de Valkey vive en Bootstrapper, como los de
/// Integrations o Storage. La regla se escribe antes de que alguien tenga la tentación de saltársela.
/// </summary>
public sealed class EphemeralStoreLayerTests
{
    // Se cargan por patrón de archivo, como en CompositionRootTests: un ancla por módulo hay que acordarse de
    // agregarla al abrir uno nuevo, y justamente el olvido es lo que esta prueba existe para atrapar.
    private static Assembly[] ModulesAndBuildingBlocks() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Modules.*.dll")
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "BuildingBlocks.*.dll"))
            .Select(Assembly.LoadFrom)
            .ToArray();

    [Fact]
    public void NoModuleOrBuildingBlockReferencesStackExchangeRedis() =>
        Assert.Empty(ModulesAndBuildingBlocks()
            .Where(assembly => assembly.GetReferencedAssemblies()
                .Any(reference => reference.Name?.StartsWith("StackExchange.Redis", StringComparison.Ordinal) == true))
            .Select(assembly => assembly.GetName().Name));

    // Sin esta, un patrón de archivo que no encuentra nada dejaría la anterior en verde por vacía.
    [Fact]
    public void TheDiscoveryFindsTheModulesAndBuildingBlocks()
    {
        var names = ModulesAndBuildingBlocks().Select(assembly => assembly.GetName().Name).ToArray();

        Assert.True(names.Length > 80, $"Sólo se encontraron {names.Length} ensamblados.");
        Assert.Contains(names, name => name?.StartsWith("BuildingBlocks.", StringComparison.Ordinal) == true);
    }

    // Ancla de que la regla mira algo real: el único que puede referenciar el paquete, lo referencia.
    [Fact]
    public void BootstrapperReferencesStackExchangeRedis() =>
        Assert.Contains(
            typeof(Bootstrapper.QepServiceCollectionExtensions).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("StackExchange.Redis", StringComparison.Ordinal) == true);
}
