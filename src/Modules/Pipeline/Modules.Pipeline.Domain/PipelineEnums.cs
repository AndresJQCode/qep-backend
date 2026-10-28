namespace Modules.Pipeline.Domain;

// Se guardan y viajan por nombre (texto): el diccionario de etiquetas lo tiene el frontend, y agregar un valor (Ai, D19) no
// necesita migración.

/// <summary>Spec 2026-10-13 D11: una etapa abierta, la de ganado o la de perdido.</summary>
public enum StageKind
{
    Open,
    Won,
    Lost,
}

/// <summary>Spec 2026-10-13 §4.2: de dónde nació el negocio.</summary>
public enum DealSource
{
    Manual,
    Conversation,
    Quotation,

    /// <summary>Spec 2026-10-15 §7: lo creó un pedido sin cotización (manual o de una integración) que no encontró negocio abierto.</summary>
    Order,
}

/// <summary>Spec 2026-10-13 D19: quién movió la etapa. <c>Ai</c> llega con el slice de IA.</summary>
public enum ActorKind
{
    User,
    System,
}

/// <summary>Spec 2026-10-13 D29: las dos acciones del triage.</summary>
public enum TriageDecisionKind
{
    Created,
    Dismissed,
}

/// <summary>Los dos catálogos de motivos del tenant.</summary>
public enum ReasonKind
{
    Loss,
    Dismiss,
}

/// <summary>Spec 2026-10-13 §4.4: el estado que Pipeline conoce de una cotización (sólo por sus eventos).</summary>
public enum DealQuotationStatus
{
    Sent,
    Converted,
}
