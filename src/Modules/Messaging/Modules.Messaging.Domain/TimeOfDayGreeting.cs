namespace Modules.Messaging.Domain;

/// <summary>El saludo que corresponde a una hora local: <c>{saludo}</c> en una campaña, donde no hay navegador que lo resuelva. Las mismas
/// franjas de las respuestas rápidas, que el frontend calcula al enviar. La hora la pone quien llama: esto no conoce husos ni relojes.</summary>
public static class TimeOfDayGreeting
{
    public static string For(TimeOnly localTime) => localTime.Hour switch
    {
        < 12 => "Buenos días",
        < 18 => "Buenas tardes",
        _ => "Buenas noches",
    };
}
