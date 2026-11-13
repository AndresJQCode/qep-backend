namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// Los tiempos de la presencia compartida (spec 2026-11-13, D-2). Cada pod reescribe a sus conectados cada
/// <see cref="HeartbeatInterval"/>; el lector sólo cuenta a quien latió dentro de <see cref="ReadWindow"/> (tres latidos, así
/// que un latido perdido no saca a nadie); y la clave vence a los <see cref="KeyTtl"/> sin latidos, para que un tenant sin
/// nadie conectado desaparezca solo. Un pod muerto por <c>SIGKILL</c> no quita a nadie: los saca la ventana de lectura.
/// </summary>
internal static class ValkeyPresence
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(90);

    public static readonly TimeSpan KeyTtl = TimeSpan.FromSeconds(120);

    /// <summary>El score de un latido: segundos Unix.</summary>
    public static double ScoreOf(DateTimeOffset now) => now.ToUnixTimeSeconds();

    /// <summary>El corte de la ventana: un score menor o igual ya no cuenta como conectado.</summary>
    public static double CutoffOf(DateTimeOffset now) => (now - ReadWindow).ToUnixTimeSeconds();
}
