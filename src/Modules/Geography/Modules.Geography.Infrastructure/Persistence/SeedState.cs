namespace Modules.Geography.Infrastructure.Persistence;

/// <summary>
/// La huella del último contenido que un importador de datos de referencia aplicó (por ejemplo <c>regions:ES</c>). Sólo
/// existe para que el arranque se salte lo que ya está al día; ver <see cref="Seed.RegionSeeder"/>.
/// </summary>
internal sealed class SeedState
{
    public string Key { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;
}
