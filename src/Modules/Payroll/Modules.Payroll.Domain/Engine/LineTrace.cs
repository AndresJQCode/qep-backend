namespace Modules.Payroll.Domain.Engine;

/// <summary>Un dato que la línea usó, con el nombre que ve el usuario en el rastro.</summary>
public sealed record TraceInput(string Name, object Value);

/// <summary>El rastro de una línea: la fórmula fija de la regla, lo que leyó, los valores legales que usó y una nota opcional.</summary>
public sealed record LineTrace(
    string Formula,
    IReadOnlyList<TraceInput> Inputs,
    IReadOnlyList<LegalValue> LegalValues,
    string? Note);
