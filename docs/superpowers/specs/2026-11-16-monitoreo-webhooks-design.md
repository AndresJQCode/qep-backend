# Monitoreo de webhooks de Meta — diseño

Fecha: 2026-11-16. Cambio chico, dos repos: `qep-backend` (métrica) y `migracion-k8s` (alertas y panel).

## Intención

Saber, sin mirar un dashboard, si los webhooks de Meta (WhatsApp, Instagram) siguen funcionando, sobre todo
después de un despliegue. Hoy el dashboard `QEP Backend` (fila «4. Webhooks») muestra tráfico, estados HTTP (incluidos
los 401 de firma), p95 y las colas, y `QepQueueLagging`/`QepQueueMetricsMissing` alertan sobre atraso. Nada avisa si los
webhooks **dejan de llegar**, si **se rechazan** ni si **llegan y se descartan**: en los tres casos la cola queda vacía y
todo se ve sano.

## Metas

1. Alerta por **rechazo**: 401 sostenidos en una ruta de webhook.
2. Alerta por **silencio**: una ruta sin 2xx durante 1 h cuando la misma hora de la semana anterior sí tuvo tráfico.
3. Alerta por **descarte silencioso** sospechoso, con una métrica nueva del backend.

## No metas

- **Shopify queda fuera por ahora** (owner, 2026-11-16: hoy ningún tenant tiene Shopify conectado). Ni contador, ni
  alertas, ni panel para `/api/webhooks/shopify`. Cuando haya un cliente conectado se suma con el mismo patrón; la
  sección «Shopify (pendiente)» deja los puntos ya identificados.
- Saber **qué** número o tienda descarta: lo dice el log que ya existe; la métrica sólo avisa.
- Pantalla en la consola de operador.
- Verificar el override de la WABA (`GET /{wabaId}/subscribed_apps`): su efecto se ve como silencio.

## Anti-metas

- Una alerta que suene todas las noches o por un ruido crónico conocido: se apaga mentalmente y deja de servir.
- Etiquetas con tenant, `phone_number_id`, dominio de tienda o datos de la persona (cardinalidad y privacidad).

## Enfoques considerados

1. **Contador en el proceso (elegido por el owner).** `qep.webhook.discarded` en cada punto donde hoy se loguea un
   descarte; panel y alertas en `migracion-k8s`. Sin migración.
2. Tabla de descartes más muestreador: diría el número o la tienda, pero exige migración, purga y otra consulta en
   `QueueMetricsSource` para un detalle que el log ya tiene.
3. Alertas sobre logs: descartado, el clúster tiene Prometheus y Tempo, no Loki.

Para el silencio se eligió **comparar con la misma hora de hace 7 días** en vez de un horario fijo (owner: de noche es
normal no recibir): se adapta solo a noches, festivos y rutas sin uso.

## Diseño

### Backend (`qep-backend`)

Contador `qep.webhook.discarded` (Prometheus: `qep_webhook_discarded_total`), etiquetas `provider` y `reason`, ambas de
conjunto cerrado. El valor sumado es el número de eventos descartados (mensajes, ecos, contactos o entregas), el mismo
`Count` que ya va al log.

| `provider`  | `reason`             | Dónde se descarta hoy                                                    | ¿Alerta? |
| ----------- | -------------------- | ------------------------------------------------------------------------ | -------- |
| `whatsapp`  | `unknown_route`      | `WebhookDeliveryProcessor` (mensajes, historial, ecos, contactos)       | Sí       |
| `whatsapp`  | `without_user_id`    | `WebhookDeliveryProcessor` (`SkippedWithoutUserId`)                      | Sí       |
| `whatsapp`  | `paused`, `module_off` | `WebhookDeliveryProcessor`                                             | No       |
| `whatsapp`  | `foreign_status`     | `WebhookDeliveryProcessor` (estado sin callback de QEP: coexistencia)    | No       |
| `whatsapp`  | `unhandled_field`    | `WebhookDeliveryProcessor` (`UnknownChange`, `user_id_update` sin ruta)  | No       |
| `instagram` | `unknown_route`      | `WebhookDeliveryProcessor.Instagram`                                     | Sí       |
| `instagram` | `paused`, `module_off` | `WebhookDeliveryProcessor.Instagram`                                   | No       |
| `instagram` | `foreign_event`      | `WebhookDeliveryProcessor.Instagram` (visto o borrado sin mensaje)       | No       |
| `instagram` | `ignored`            | `WebhookDeliveryProcessor.Instagram` (`InstagramIgnoredEvent`)           | No       |
- El contador va en `MessagingMetrics` (ya existe, meter `Qep.Messaging`), junto a cada `LogDiscarded`,
  `LogInstagramDiscarded`, `LogSkippedWithoutUserId`, `LogForeignStatus`, `LogInstagramForeign`, `LogIgnoredField` y
  `LogInstagramIgnored`. Los valores de `reason` pasan a `snake_case` en la métrica; el texto de los logs no cambia.
- `QepObservability` ya exporta `Qep.*`: no se toca el exportador.
- Los motivos viven como constantes (`WebhookDiscardReasons`); una prueba unitaria fija el conjunto, porque las alertas de
  `migracion-k8s` los leen por nombre.

### Shopify (pendiente)

Cuando haya un tenant conectado: clase de métricas propia en Infrastructure (meter `Qep.Shopify`), con el mismo contador y
`provider = shopify`, registrada donde se conoce el resultado (el endpoint para `ShopifyWebhookReceipt.DiscardReason` y el
worker para `ShopifyDeliveryResult.Ignored`). Alertarían `unknown_shop`, `shop_domain_mismatch`, `missing_headers`,
`header_too_long` e `invalid_json`; no `unsupported_topic`. Más las mismas tres alertas con la ruta `/api/webhooks/shopify`.

### Plataforma (`migracion-k8s`)

En `qep-backend-prometheusrule.yaml`, por ruta (`/api/webhooks/whatsapp`, `/api/webhooks/instagram`):

- `QepWebhookSignatureRejected`: tasa de 401 > 0 durante 10 min.
- `QepWebhookSilent`: `increase(2xx[1h]) == 0` y `increase(2xx[1h] offset 7d) > 10`.
- `QepWebhookDiscarding`: descartes de motivos sospechosos en 1 h > 3 × los de la misma hora hace 7 días + 5. La
  comparación con la semana anterior es para no sonar por ruido crónico conocido (el `phone_number_id`
  1100135673190922 que llega sin conexión).

En `qep-backend-dashboard.yaml`, fila «4. Webhooks»: panel «Descartados por motivo» (`provider`, `reason`).

Los nombres exactos de la métrica HTTP y del label de ruta se toman del dashboard existente al escribir las reglas.

## Restricciones

- Los motivos son contrato con las alertas: renombrar uno compila y deja la alerta muda. La prueba de conjunto lo amarra.
- Despliegue: el backend primero; las reglas que leen `qep_webhook_discarded_total` dan vacío (no alertan) hasta entonces.
- `migracion-k8s` lleva su propio spec corto apuntando a este.

## Decisiones del builder (marcadas)

- Umbrales (10 min, > 10, 3× + 5) son propuesta inicial; se ajustan con una semana de datos.
- `foreign_status` no alerta: en coexistencia es tráfico normal.
