namespace Modules.Support.Domain;

/// <summary>El estado de una respuesta del ticket (plan E16). Se persiste por nombre. La fila nace <see cref="Pending"/> y se
/// confirma antes de llamar afuera, para que un reintento con el mismo <c>clientId</c> no duplique el envío.</summary>
public enum SupportReplyStatus
{
    Pending,
    Sent,
    Failed,
}
