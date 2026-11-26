# Siigo: el motivo de una factura que sigue pendiente — diseño

Fecha: 2026-11-21 · Repo: `qep-backend` · Estado: **propuesto; el owner eligió la opción de guardar el motivo en `ErrorCode` (2026-11-21)**

## Intención

Cuando Siigo no contesta a tiempo, no hay red, o responde 429 o 5xx, la factura queda `Pending` y se reintenta con los mismos bytes y la
misma clave. En el pedido el tenant ve el badge «Pendiente» y el botón «Reintentar», **sin saber qué pasó**: `SiigoInvoice.MarkTransient`
solo suelta el reclamo y el motivo (`SiigoInvoiceOutcome.Transient.Reason`) se pierde. Los demás estados con falla (`Rejected`, `Failed`,
`StampRejected`, `Unknown`) ya muestran su mensaje en español.

## Metas

- La fila `Pending` guarda por qué falló el último intento, y el DTO lo entrega a la pantalla como un código `siigo.invoice.*`.
- Sin migración: se usa la columna `error_code` que ya existe (`ErrorCodeMaxLength = 100`).

## No metas

- No cambia `siigo.call_failures` (el diagnóstico técnico con curl) ni las métricas de Grafana.
- No cambia el reintento, la clave de idempotencia ni la transición `Pending` → `Unknown` a las 24 h (`StaleAfter`).
- No se guarda `ErrorDetail` para estos casos: no hay texto de Siigo que mostrar.

## Diseño

- `SiigoInvoice.MarkTransient(string? errorCode, DateTimeOffset now)`: además de soltar el reclamo, fija `ErrorCode` (con `Cap`) y limpia
  `ErrorDetail`. Con `errorCode` nulo no toca lo que ya había (el caso de la factura de flete que espera a la del pedido, que no es una
  falla propia).
- Traducción del motivo, en `SiigoInvoiceSupport` junto a `ErrorCodeOf`:

| `Transient.Reason` | `ErrorCode` |
|---|---|
| `timeout` | `siigo.invoice.timeout` |
| `network` | `siigo.invoice.network` |
| `http_429` | `siigo.invoice.rate_limited` |
| cualquier otro (`http_5xx`, `partner_id_missing`, `auth_without_token`, …) | `siigo.invoice.unavailable` |

  Las constantes van en `SiigoErrorCodes`. Los motivos de configuración (`partner_id_missing`, `auth_without_token`) caen en `unavailable`
  a propósito: el tenant no puede corregirlos, y quien sí los ve en Grafana (`qep_siigo_call_failures_total`).
- `IssueSiigoInvoice` (rama `default:` de la respuesta, que hoy llama `row.MarkTransient(now)`) pasa el código traducido del
  `SiigoInvoiceOutcome.Transient`. La llamada de la factura de flete que espera (`row.MarkTransient(clock.UtcNow)`) pasa `null`.
- Cuando el reintento sale bien (`MarkCreated`/`MarkRejected`) el código se limpia o se reemplaza, como ya hacen `ClearError()` y `MarkRejected`.
  `Unknown` conserva el último código sin mostrarlo.
- `SiigoInvoiceDto` no cambia: `errorCode` ya viaja. BFF: el motivo es lo que la pantalla necesita para decir qué hacer.

## Pruebas

- `SiigoInvoiceTests`: `MarkTransient` con código lo guarda y limpia el detalle; con `null` conserva el anterior; un `MarkCreated`
  posterior lo limpia.
- `SiigoInvoiceSupportTests`: la tabla de arriba, incluida una razón desconocida.
- `IssueSiigoInvoice` (unitaria o integración): un timeout deja la fila `Pending` con `siigo.invoice.timeout`, y la lectura de las
  facturas del pedido lo devuelve en `errorCode`.
- La fila de flete que espera no cambia su `ErrorCode`.

## Otros repos

- **`qep-frontend`:** el caso `Pending` de `SiigoInvoiceCard` muestra el mensaje por código (spec aparte, mismo nombre).
- Sin cambios en `migracion-k8s`.
