# Mensaje no disponible — handoff al frontend

**Qué cambió en el backend (2026-10-14).** `MessageDto` trae un campo nuevo, `unsupportedReason: string | null`.
Sólo viene lleno en un mensaje `kind: "Unsupported"` cuyo error de Meta tiene texto; en cualquier otro caso es
`null`. Igual que `failureReason`, viaja **en español y listo para mostrar**: el frontend no conoce los códigos de
Meta y no tiene que conocerlos.

| Código de Meta | `unsupportedReason` |
| --- | --- |
| `131060` (Meta no entregó el contenido) | «WhatsApp no entregó el contenido de este mensaje. Pídele a la persona que lo envíe de nuevo.» |
| `131051` (tipo que la Cloud API no entrega) | «WhatsApp no admite este tipo de mensaje. Pídele a la persona que lo envíe como texto, imagen o documento.» |
| cualquier otro, o sin código | `null` |

**Por qué.** El 2026-10-14 un cliente escribió «hola» y la bandeja mostró «Mensaje no compatible». Meta había
mandado `type: "unsupported"` con el error `131060`: la persona sí escribió, pero el contenido no llegó. «Mensaje no
compatible» le dice al asesor que el cliente mandó algo raro; lo que necesita saber es que tiene que pedirle que lo
reenvíe.

## Lo que hay que tocar en `qep-frontend`

1. **Tipo** (`src/features/messaging/types/messaging.ts`, `interface Message`): agregar
   `unsupportedReason: string | null`.
2. **Parser** (`src/features/messaging/services/messaging.api.ts`, junto a `failureReason`): leerlo como texto
   opcional. Un backend anterior no lo manda, así que la ausencia vale `null` —igual que `template`—; no es un error
   de contrato.
3. **Burbuja** (`src/features/messaging/components/message-bubble.tsx`, el retorno final de «a kind without a
   renderer»): con `kind === 'Unsupported'` y `unsupportedReason` lleno, dibujar ese texto en lugar de
   `MESSAGE_KIND_LABELS.Unsupported`. Con `null`, queda como está.
4. **Fixtures** (`src/test/messaging.ts`): el mensaje de ejemplo lleva `unsupportedReason: null`.

## Lo que **no** cambia

- `kind` sigue siendo `Unsupported`: no hay un kind nuevo, así que `MESSAGE_KINDS` no se toca.
- La **lista de conversaciones** (`lastMessage`) no trae el motivo: la fila sigue diciendo «Mensaje no compatible».
  Mostrarlo ahí exige guardarlo en `messaging.conversations` (migración); queda como pendiente si hace falta.
- El texto del mensaje original no se puede recuperar: Meta no lo reenvía.
