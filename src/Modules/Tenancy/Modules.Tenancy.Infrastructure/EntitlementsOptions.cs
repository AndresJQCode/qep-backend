namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Spec 2026-10-07, «Alta por signup». <c>register-tenant</c> lo puede llamar cualquiera con un
/// token de Google, así que darle módulos comerciales a ese tenant es una decisión de negocio y se
/// configura aparte. Vive en Infrastructure porque ninguna capa Application del repo usa
/// <c>IOptions</c>. Sin validador: un <c>bool</c> no tiene valores inválidos; el binding corre al
/// arrancar con <c>ValidateOnStart</c>, así que <c>"si"</c> tumba el arranque y no el primer signup.
/// </summary>
public sealed class EntitlementsOptions
{
    public const string SectionName = "Entitlements";

    /// <summary><c>true</c> (default, el comportamiento de hoy): el signup da los seis módulos sin
    /// <c>pos</c>. <c>false</c>: no da ninguno (sólo núcleo). Cuando exista el cobro, lo esperable es
    /// que QCode lo ponga en <c>false</c> y prenda los módulos al pagar.</summary>
    public bool GrantDefaultModulesOnSignup { get; set; } = true;
}
