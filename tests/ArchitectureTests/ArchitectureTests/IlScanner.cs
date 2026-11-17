using System.Reflection;

namespace ArchitectureTests;

/// <summary>
/// Lee el IL de un ensamblado para saber quién llama o crea qué, sin librerías de análisis. Busca los opcodes de llamada (<c>call</c>,
/// <c>callvirt</c>, <c>newobj</c>) y resuelve su token; un byte que no es un opcode pero lo parece da un token que no
/// resuelve y se ignora, así que puede sobrar pero nunca faltar una llamada real.
/// </summary>
internal static class IlScanner
{
    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Los métodos de <paramref name="assembly"/> cuyo cuerpo llama o crea algo que cumple <paramref name="target"/>.</summary>
    public static IReadOnlyList<MethodBase> CallersOf(Assembly assembly, Func<MethodBase, bool> target) =>
        [.. MethodsOf(assembly.GetTypes()).Where(method => CalleesOf(method).Any(target))];

    /// <summary>Lo que llama o crea el cuerpo de <paramref name="method"/>.</summary>
    public static IEnumerable<MethodBase> CalleesOf(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var index = 0; index < il.Length - 4; index++)
        {
            // call, callvirt y newobj. El cuerpo de una lambda es un método propio, así que no hace falta seguir los ldftn.
            if (il[index] is not (0x28 or 0x6F or 0x73))
            {
                continue;
            }

            MethodBase? callee;
            try
            {
                callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1), typeArguments, methodArguments);
            }
            catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or TypeLoadException or MissingMethodException)
            {
                callee = null;
            }

            if (callee is not null)
            {
                yield return callee;
            }
        }
    }

    /// <summary>Todos los métodos y constructores declarados en <paramref name="types"/> y en sus anidados (las máquinas de estado de los
    /// <c>async</c> y las clausuras de las lambdas son tipos anidados).</summary>
    public static IEnumerable<MethodBase> MethodsOf(IEnumerable<Type> types) =>
        types.SelectMany(type => type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)));

    /// <summary>Si <paramref name="method"/> es de <paramref name="owner"/> o de un tipo anidado en él (incluidos los del compilador).</summary>
    public static bool IsWithin(MethodBase method, Type owner)
    {
        for (var type = method.DeclaringType; type is not null; type = type.DeclaringType)
        {
            if (type == owner)
            {
                return true;
            }
        }

        return false;
    }
}
