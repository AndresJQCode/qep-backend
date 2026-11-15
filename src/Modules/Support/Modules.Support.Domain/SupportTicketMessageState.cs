namespace Modules.Support.Domain;

/// <summary>El estado del vínculo de un mensaje del hilo con un ticket (plan E9). Se persiste por nombre. <see cref="Linked"/> lo
/// vinculó el seguimiento del hilo y se muestra plegado; <see cref="Pinned"/> lo fijó el asesor y se muestra en línea;
/// <see cref="Released"/> quedó fuera del expediente y el seguimiento nunca lo vuelve a vincular.</summary>
public enum SupportTicketMessageState
{
    Linked,
    Pinned,
    Released,
}
