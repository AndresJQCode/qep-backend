using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Plantillas editables: el envío del sistema se arma desde la fila aprobada del espejo, no desde una definición fija. Puro: recibe
/// los valores ya resueltos y devuelve lo que viaja a Meta, el texto y el <c>details</c>.</summary>
public static class SystemTemplateSend
{
    /// <summary>Las variables con nombre que usa la fila, en su orden, ya en la clave nueva (una clave vieja se traduce). Las posicionales no
    /// aparecen: su orden lo da quien llama con <c>legacyPositionalKeys</c>.</summary>
    public static IReadOnlyList<string> VariablesOf(ApprovedTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return [.. Describe(template).Parameters.Where(key => !IsPositional(key)).Select(ValueKeyOf)];
    }

    /// <summary>Arma el envío con las variables que la fila usa y en su orden, o <c>null</c> si a alguna le falta el valor o no cumple las
    /// reglas de Meta. <paramref name="values"/> va por clave nueva (<c>nombre_contacto</c>); <paramref name="legacyPositionalKeys"/> es el
    /// orden de una fila posicional vieja (<c>{{1}}</c> es la primera).</summary>
    public static BuiltTemplate? Build(
        ApprovedTemplate template, IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? legacyPositionalKeys = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var described = Describe(template);

        // No se lee la columna: el refresco guarda POSITIONAL cuando Meta no manda parameter_format, y mandar con nombre unas claves con
        // nombre como si fueran posicionales es un 400 de Meta. Lo que manda es la forma de las claves.
        var named = described.Parameters.Any(key => !IsPositional(key));

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in described.Parameters)
        {
            var value = ValueOf(key, named, values, legacyPositionalKeys);
            if (!TemplateParameterRules.IsValid(value))
            {
                return null;
            }

            // La clave de la fila, no la española: el details y el parameter_name de Meta llevan lo que la plantilla aprobada declara.
            resolved[key] = value!;
        }

        return TemplateCatalog.BuildSend(
            described with { ParameterFormat = named ? TemplateCatalog.Named : TemplateCatalog.Positional }, resolved);
    }

    private static MessageTemplateDto Describe(ApprovedTemplate template) =>
        TemplateCatalog.FromStored(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson);

    private static bool IsPositional(string key) => key.All(char.IsAsciiDigit);

    // Las cuatro claves viejas de la plantilla de cotización sólo sobreviven acá: la fila ya aprobada en Meta no se puede renombrar, pero el
    // valor se resuelve por la clave nueva.
    private static string ValueKeyOf(string key) => SystemTemplateVariables.LegacyKeys.GetValueOrDefault(key, key);

    private static string? ValueOf(
        string key, bool named, IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? legacyPositionalKeys)
    {
        if (named)
        {
            return values.GetValueOrDefault(ValueKeyOf(key));
        }

        // TryParse: una clave vacía o enorme viene de una fila de Meta y no puede tumbar el envío.
        if (!int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var position = number - 1;
        return legacyPositionalKeys is not null && position >= 0 && position < legacyPositionalKeys.Count
            ? values.GetValueOrDefault(legacyPositionalKeys[position])
            : null;
    }
}

/// <summary>Plantillas editables: los valores que dependen de quién envía y de cuándo (<c>asesor</c>, <c>empresa</c>, <c>saludo</c>). Sólo busca
/// los que la fila usa: un dato que la plantilla no lleva no justifica una consulta.</summary>
public sealed class TemplateSenderValues(IMessagingMemberNames memberNames, IMessagingTenantNames tenantNames, ITenantClock tenantClock)
{
    public async Task AddAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<string> usedKeys, IDictionary<string, string> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usedKeys);
        ArgumentNullException.ThrowIfNull(values);

        string? company = null;
        if (usedKeys.Contains(SystemTemplateVariables.Company) || usedKeys.Contains(SystemTemplateVariables.Advisor))
        {
            company = await tenantNames.FindAsync(tenantId, cancellationToken);
        }

        if (usedKeys.Contains(SystemTemplateVariables.Company) && company is not null)
        {
            values[SystemTemplateVariables.Company] = company;
        }

        if (usedKeys.Contains(SystemTemplateVariables.Advisor))
        {
            // Quien envía sin nombre visible no deja la variable vacía: Meta rechaza un parámetro en blanco. Firma la empresa.
            var advisor = await memberNames.FindDisplayNameAsync(tenantId, memberId, cancellationToken);
            var name = TemplateParameterRules.IsValid(advisor) ? advisor : company;
            if (name is not null)
            {
                values[SystemTemplateVariables.Advisor] = name;
            }
        }

        if (usedKeys.Contains(SystemTemplateVariables.Greeting))
        {
            var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
            values[SystemTemplateVariables.Greeting] = TimeOfDayGreeting.For(TimeOnly.FromDateTime(calendar.ToLocal(calendar.UtcNow).DateTime));
        }
    }
}
