# Shopify: días de historial para «Sincronizar ahora»

Fecha: 2026-10-21. Estado: **diseño acordado en conversación, sin implementar**. Extiende el spec
[2026-10-15-integracion-shopify-pedidos-design.md](2026-10-15-integracion-shopify-pedidos-design.md), que dejó
fuera el historial en D-S4 («Un botón “Traer los últimos N días”»).

## Por qué

Un pedido creado en Shopify **antes** de enlazar la tienda nunca entra: la conciliación pide a Shopify los pedidos con
`updated_at ≥ last_reconciled_at − 5 min` (`ReconcileStore.RunAsync`) y `ShopifyStore.Create` fija esa ventana en el instante del
enlace (`LinkedAt`). «Sincronizar ahora» responde con cero traídos y sin error, que es indistinguible de «no hay nada».
Síntoma que lo originó (2026-10-20): un pedido creado antes de configurar la integración no aparecía tras
`POST …/shopify/stores/{id}/sync`.

## Qué se construye

Un campo opcional **«Días de historial»** (`historyDays`) en la configuración de la tienda
(`/integrations/shopify/{connectionId}` en el frontend). Con valor, «Sincronizar ahora» trae también los pedidos de los N días
anteriores al enlace; sin valor, todo sigue como hoy (D-S4).

### Decisiones del owner (conversación del 2026-10-21)

- **Tope 30 días.** `historyDays` ∈ [1, 30] o `null`.
- **Cuenta desde el enlace**, no desde hoy: la fecha de partida es `LinkedAt − N días`.
- **Se aplica en cada pulsación del botón**, no una sola vez. El objetivo no es reimportar sino **importar lo que faltaba**
  (incluye lo que falló o quedó en `Waiting` antes). Por eso no hay bandera de «historial ya traído».

### Decisiones mías (marcadas para revisión)

- **El cron no cambia.** Sólo la corrida manual (`SyncShopifyStoreHandler`) usa el historial; `ShopifyReconcileWorker` sigue con la
  ventana normal. Así el cron no relee 30 días cada ciclo.
- **La ventana nunca se acorta:** `since = min(ventanaNormal, LinkedAt − N días)`, con
  `ventanaNormal = (LastReconciledAt ?? startedAt) − Overlap`. Si el enlace es más viejo que N días, `LinkedAt − N` queda en el
  pasado y gana; si la ventana normal ya es más vieja, gana ella.
- **`last_reconciled_at` se mueve igual que hoy:** sólo si la corrida termina sin fallos, a `startedAt`.
- **Sin `LinkedAt`** (`ShopifyConnectionRef.LinkedAt` es nullable): se ignora el historial y se usa la ventana normal. No se inventa una fecha.

## Por qué es seguro releer lo ya importado

Verificado en el código, no supuesto:

- `ShopifyOrderMirror.ApplySnapshot` acepta un snapshot de `updated_at` igual (sólo rechaza uno **más viejo**) y no toca el estado de importación.
- `ShopifyOrderIngestion.ApplyOnceAsync` importa únicamente si la fila sigue `Waiting` y `applied`; una fila `Imported` solo recibe el guardado del espejo.
- La reacción de pago/cancelación (§6.3) compara contra el estado anterior: un snapshot igual no dispara nada.
- Si algo fallara antes, `IX_orders_tenant_external_key` deja un solo pedido por `(tenant_id, source, external_key)`.

Costo: con el tope de 30 días, el peor caso es releer 30 días de pedidos por pulsación, y sólo con el campo configurado.

## Cambios

**Domain** (`ShopifyStore`): propiedad `int? HistoryDays`, constante `HistoryDaysMax = 30`; `Configure` la recibe y valida el rango
(`ArgumentOutOfRangeException` fuera de 1..30). Sube `Version` como el resto del `PUT`.

**Application**
- `UpdateShopifyStoreCommand` + `UpdateShopifyStoreValidator`: `HistoryDays` opcional, 1..30. Es entrada del usuario: lleva
  validador aunque el dominio valide (regla del repo: el dominio da un código, el validador da el campo → 422 `validation.failed` con `errors`).
- `ShopifyStoreDto` devuelve `historyDays` (nullable) y el handler lo incluye en `ComparedFields`, para que el cambio quede en la auditoría.
- `ReconcileStore.RunAsync` recibe un parámetro de modo (manual con historial / cron). `SyncShopifyStoreHandler` pide el manual;
  `ShopifyReconcileWorker` el normal. El cálculo de `since` es una función pura y estática (`ComputeSince`) para probarla sin Shopify.

**Infrastructure:** migración `AddStoreHistoryDays` — columna `history_days integer NULL` con `CHECK (history_days BETWEEN 1 AND 30)`,
generada con el factory de diseño (ver `CLAUDE.md`, «Las migraciones se generan con el factory de diseño»). Sin backfill: `NULL` = comportamiento actual.
Revisar que ningún `FromSql` crudo de `shopify.stores` deje fuera la columna nueva (gotcha de `FromSql`).

**Frontend** (`qep-frontend`, repo aparte; verificar rama antes de leerlo): campo numérico «Días de historial» (vacío = sin historial,
1–30) en la pantalla de la tienda, con una línea que diga que «Sincronizar ahora» traerá desde esa cantidad de días antes del enlace.
El resultado de la sincronización ya muestra los conteos; no cambia.

## Fuera de alcance

- Historial mayor a 30 días o por rango de fechas.
- Que el cron use el historial.
- Un botón aparte «Traer historial»: el campo reutiliza «Sincronizar ahora».

## Plan de pruebas (TDD, RED antes que GREEN)

1. `ComputeSince`: sin `HistoryDays` = ventana normal; con N y enlace viejo = `LinkedAt − N`; ventana normal más vieja que `LinkedAt − N` = la normal; sin `LinkedAt` = la normal; modo cron ignora N.
2. Dominio: `Configure` rechaza 0, 31 y negativos; acepta `null`, 1 y 30.
3. Validador: 422 `validation.failed` con `errors` para `historyDays` fuera de rango.
4. Integración (`ShopifyApiHarness`): pedido anterior al enlace con N configurado → el sync lo importa; sin N → `fetched = 0`; segundo sync no duplica ni cambia lo `Imported`; un `Waiting` del historial se importa al corregir su causa y volver a pulsar.
5. Auditoría: cambiar `historyDays` aparece en el registro del `PUT`.
6. Migración: sube y baja sin romper (volver a la última antes de llamar a la API).

Pruebas acotadas al módulo Shopify; no se corre la suite completa.
