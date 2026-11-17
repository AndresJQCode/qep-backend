using System.Reflection;
using Bootstrapper;
using BuildingBlocks.Application;

namespace ArchitectureTests;

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C4 y D-C15, plan fase 2 C2.5: el copiloto sólo lee. Ningún tipo de <c>Bootstrapper.Copilot</c> despacha un
/// comando ni crea uno, toda herramienta despacha alguna consulta, y ninguna llega a Integrations (claves, conexiones) ni a las consultas
/// de operador. Se lee del IL, así que una herramienta nueva que escriba pone esto en rojo aunque nadie la pruebe.
/// </summary>
public sealed class CopilotToolsTests
{
    private static readonly Assembly Bootstrapper = typeof(QepServiceCollectionExtensions).Assembly;

    [Fact]
    public void EveryCopilotToolDispatchesAQueryAndNeverACommand()
    {
        var copilot = CopilotTypes();
        var callees = IlScanner.MethodsOf(copilot).SelectMany(IlScanner.CalleesOf).ToList();

        Assert.DoesNotContain(callees, callee => callee.DeclaringType == typeof(IRequestDispatcher) && callee.Name == nameof(IRequestDispatcher.SendAsync));
        Assert.DoesNotContain(callees, callee => callee is ConstructorInfo && Implements(callee.DeclaringType!, typeof(ICommand<>)));

        var tools = Tools();
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var own = IlScanner.MethodsOf(TypeAndNested(tool)).SelectMany(IlScanner.CalleesOf).ToList();
            Assert.True(
                own.Any(callee => callee.DeclaringType == typeof(IRequestDispatcher) && callee.Name == nameof(IRequestDispatcher.QueryAsync)),
                $"{tool.Name} does not dispatch a query.");
            Assert.All(
                own.Where(callee => callee is ConstructorInfo && callee.DeclaringType!.Name.EndsWith("Query", StringComparison.Ordinal)),
                query => Assert.True(Implements(query.DeclaringType!, typeof(IQuery<>)), $"{query.DeclaringType!.Name} is not an IQuery<>."));
        }
    }

    [Fact]
    public void NoCopilotToolTouchesIntegrationsOrOperatorQueries()
    {
        var callees = IlScanner.MethodsOf(CopilotTypes()).SelectMany(IlScanner.CalleesOf).ToList();

        Assert.DoesNotContain(callees, callee =>
            callee.DeclaringType?.Assembly.GetName().Name?.StartsWith("Modules.Integrations", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(callees, callee =>
            callee is ConstructorInfo && Implements(callee.DeclaringType!, typeof(IQuery<>))
            && callee.DeclaringType!.Name.Contains("Operator", StringComparison.Ordinal));
        Assert.DoesNotContain(
            CopilotTypes().SelectMany(type => type.GetReferencedTypes()),
            referenced => referenced.Assembly.GetName().Name?.StartsWith("Modules.Integrations", StringComparison.Ordinal) == true);
    }

    private static Type[] CopilotTypes() =>
        [.. Bootstrapper.GetTypes().Where(type => type.Namespace == "Bootstrapper.Copilot")];

    private static Type[] Tools()
    {
        var contract = Bootstrapper.GetType("Bootstrapper.Copilot.ICopilotTool", throwOnError: true)!;
        return [.. CopilotTypes().Where(type => type is { IsClass: true, IsAbstract: false } && contract.IsAssignableFrom(type))];
    }

    private static IEnumerable<Type> TypeAndNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(TypeAndNested));

    private static bool Implements(Type type, Type openGeneric) =>
        type.GetInterfaces().Any(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == openGeneric);
}

internal static class TypeReferences
{
    /// <summary>Los tipos de los campos, propiedades y parámetros de constructores de <paramref name="type"/>: lo que puede recibir por DI.</summary>
    public static IEnumerable<Type> GetReferencedTypes(this Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetFields(all).Select(field => field.FieldType)
            .Concat(type.GetProperties(all).Select(property => property.PropertyType))
            .Concat(type.GetConstructors(all).SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
    }
}
