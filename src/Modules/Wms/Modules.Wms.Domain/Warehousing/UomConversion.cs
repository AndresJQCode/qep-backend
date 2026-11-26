namespace Modules.Wms.Domain.Warehousing;

/// <summary>Presentación de una variante («CAJA x12»): cuántas unidades base trae. La crea y la reemplaza el perfil.</summary>
public sealed class UomConversion
{
    public const int CodeMaxLength = 32;

    private UomConversion()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public decimal Factor { get; private set; }

    public string? Barcode { get; private set; }

    internal static UomConversion Create(Guid id, string code, decimal factor, string? barcode) =>
        new() { Id = id, Code = code, Factor = factor, Barcode = barcode };

    /// <summary>Cambia los datos conservando el Id: lo llama el perfil al editar, para que EF la vea como la misma fila.</summary>
    internal void Reconfigure(string code, decimal factor, string? barcode)
    {
        Code = code;
        Factor = factor;
        Barcode = barcode;
    }
}
