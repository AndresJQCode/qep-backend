namespace Modules.Identity.Domain;

/// <summary>
/// Eje de luminosidad de la preferencia de apariencia. Es un conjunto cerrado a propósito:
/// son dos valores y el spec de <c>ACC-03</c> los fija. Un tercer modo "Sistema" —que seguiría
/// <c>prefers-color-scheme</c>— está registrado como <c>SDD-OD-20</c>, todavía abierta, y
/// entraría por un slice propio: agrega un estado a persistir y un caso a probar.
/// </summary>
public enum ThemeMode
{
    Light,
    Dark,
}
