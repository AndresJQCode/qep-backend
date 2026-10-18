# Integración con Shopify: handoff para el owner

Lo que tienes que hacer tú para que la integración de pedidos de Shopify (spec
`docs/superpowers/specs/2026-10-15-integracion-shopify-pedidos-design.md`) funcione con una tienda real, y lo que queda
por decidir. El backend está completo en `feature/shopify` (plan `2026-10-18-shopify-backend.md`, tareas 1 a 17) y
probado contra fakes y fixtures **sintéticos**: nada de esto habló todavía con Shopify. Las verificaciones V-1 a V-10 de
§16 son las que cierran esa distancia.

Todo comando va en PowerShell. **Ninguno imprime un secreto**: los `set` van con `| Out-Null` y las verificaciones
cuentan o describen, nunca listan valores (CLAUDE.md, reglas duras).

## 1. La app en el Dev Dashboard de Shopify (§10.5)

1. En el Dev Dashboard de Shopify crea una app **pública** y **no embebida** (distribución pública,
   listada o no: las dos pasan por la revisión de Shopify). No marques «Embed app in Shopify admin»: QEP abre su propia
   pestaña y el flujo es el *authorization code grant* con redirección (V-1).
2. **App URL:** `https://qep.qcode.co/shopify/install`. Es la ruta pública del SPA que recibe el inicio B (instalar desde
   Shopify).
3. **Allowed redirection URLs**, las dos:
   - `https://qep.qcode.co/shopify/callback`
   - `http://localhost:3002/shopify/callback`
4. **Scopes:** `read_orders,read_customers` (los mismos de `Shopify:App:Scopes`; si Shopify concede menos, el canje
   responde `integrations.shopify.scopes_missing`).
5. **Webhooks de cumplimiento**, los tres a `https://qep-api.qcode.co/api/webhooks/shopify`: `customers/data_request`,
   `customers/redact` y `shop/redact`. El host es el del ingress (`k8s/prod-ingress.yaml:24`) y la URL es la misma de
   `Shopify:App:WebhookCallbackUrl`. Shopify no deja registrarlos por API: si faltan, la app no pasa la revisión.
   Los otros cinco (`orders/create`, `orders/updated`, `orders/paid`, `orders/cancelled`, `app/uninstalled`) los
   registra QEP en cada tienda al enlazarla.
6. Anota el **Client ID** y el **Client secret** de la app. El secreto no lo pegues en ningún chat, issue ni commit.

## 2. Acceso a datos protegidos de clientes (V-3)

En la configuración de la app, pide **Protected customer data access**, con los campos de nombre, correo, teléfono y dirección.
Sin la aprobación, los pedidos llegan sin datos del comprador: todo cliente nace incompleto y sin forma de encontrarlo
después (no hay teléfono ni correo con qué emparejarlo). Pídelo antes de la tienda piloto; la aprobación puede tardar.

## 3. Credenciales: pipeline y local

**Producción.** En el grupo de variables `Backend-prod` de Azure DevOps:

| Variable | Tipo | Va a |
| --- | --- | --- |
| `SHOPIFY_APP_CLIENT_ID` | normal | `Shopify__App__ClientId` en `k8s/prod-configMap.yaml` |
| `SHOPIFY_APP_CLIENT_SECRET` | **secreta** (candado) | `Shopify__App__ClientSecret` en `k8s/prod-secret.yaml` |

`azure-pipelines.yml` no las nombra: la plantilla de despliegue reemplaza cada `#{NOMBRE}#` de `k8s/` con la variable del
grupo que se llame igual. Si una falta, el token llega literal y `ShopifyAppOptionsValidator` deja el pod en crash-loop
a propósito (tarea 17: antes lo daba por configurado y el pod arrancaba rechazando todas las firmas, el mismo hueco que
CLAUDE.md describe para Instagram). Para confirmar que el Secret tiene la clave **sin ver su valor**:

```powershell
kubectl --context contabo-prod -n prod-qep-backend describe secret qep-backend-secret | Select-String -Pattern "Shopify__App__ClientSecret"
```

Tiene que salir una línea con el tamaño en bytes (no `0`). Nunca `get secret -o yaml`, `-o json` ni `custom-columns`.

**Local.** `dotnet user-secrets set` imprime la clave y su valor, así que van con `| Out-Null`, y la verificación cuenta:

```powershell
dotnet user-secrets set "Shopify:App:ClientId" "<client-id-de-la-app>" --project src/Api | Out-Null
dotnet user-secrets set "Shopify:App:ClientSecret" "<client-secret-de-la-app>" --project src/Api | Out-Null
dotnet user-secrets list --project src/Api | Select-String -Pattern "Shopify:App" | Measure-Object
```

El último comando tiene que dar `Count 2`. `appsettings.Development.json` ya trae `http://localhost:3002` como origen.
Sin túnel, Shopify no puede entregar webhooks a tu máquina: la conciliación (cada 10 minutos) y «Sincronizar ahora»
traen los pedidos igual. Con túnel, fija la URL del webhook en esa sesión:

```powershell
$env:Shopify__App__WebhookCallbackUrl = "https://<tu-tunel>/api/webhooks/shopify"
dotnet run --project src/Api
```

**Frontend.** `VITE_SHOPIFY_APP_URL` con el enlace de instalación de la app (spec §10.4), en el plan del frontend.

## 4. Despliegue y tienda de desarrollo (V-1, V-2, V-7)

1. Despliega el backend (migraciones en el README, § Integración con Shopify) y después el frontend.
2. Crea una **tienda de desarrollo** en el Dev Dashboard.
3. **Inicio A, desde QEP (V-1):** en un tenant con `shopify` prendido, Integraciones → Shopify → escribe el dominio
   `<tienda>.myshopify.com` → autoriza en Shopify → vuelves a QEP y la tienda queda enlazada. Que funcione de punta a
   punta **es** V-1: Shopify sigue aceptando el *authorization code grant* con token offline para una app no embebida.
4. **Inicio B, desde la tienda (V-7):** desinstala, y vuelve a instalar desde el admin de la tienda (o con el enlace de
   instalación). Shopify tiene que abrir `https://qep.qcode.co/shopify/install?shop=…&hmac=…&timestamp=…&host=…`; si la
   pantalla dice «Abre QEP desde tu tienda de Shopify», el HMAC no cuadró: anota los parámetros exactos que trajo la URL
   (sin el `hmac`) para ajustar `ShopifyHmac`. La tarea 6 dejó sin verificar si Shopify agrega parámetros que no
   conocemos.
5. **Webhooks registrados (V-2):** la conexión tiene que quedar `Active` y con cinco ids de suscripción:

   ```sql
   SELECT status, last_failure_code, fields ->> 'webhookSubscriptionIds' AS ids
   FROM integrations.connections WHERE provider_key = 'shopify';
   ```

   `fields` no tiene secretos (el token está cifrado en `connection_secrets`). Con `NeedsAttention` y
   `webhooks_not_registered`, Shopify rechazó el registro: «Probar conexión» lo reintenta. Que el primer pedido llegue
   firmado y entre (paso 5) cierra la otra mitad de V-2: las entregas se firman con el client secret.

## 5. Pedidos de prueba y qué mirar en cada uno

Configura primero la tienda en QEP (asesor, cuenta de cobro en COP, clasificación por defecto) y crea en Catálogo
productos cuyo **código** sea el SKU de Shopify. Para ver cada pedido en el espejo:

```sql
SELECT order_name, import_state, wait_reason, wait_detail, warnings, last_error, financial_status, total_difference
FROM shopify.orders ORDER BY shopify_created_at DESC LIMIT 20;
```

| Pedido | Qué mirar | V |
| --- | --- | --- |
| Pendiente (transferencia o pago manual) que después marcas como pagado | Que lleguen `orders/paid` **y** `orders/updated` (dos filas en `shopify.webhook_deliveries` con esos `topic`), y que el pedido de QEP pase de `PaymentPending` a `FullPaymentReceived` | V-5 |
| Recogida en tienda | El `code` de la línea de envío: QEP la trata como recogida si empieza por `pickup`. Si trae otro, anótalo | V-6 |
| Contraentrega | Anota `payment_gateway_names` y `financial_status` del payload crudo; QEP reconoce `cash on delivery (cod)`, `contra entrega`, `contraentrega` y `pago contra entrega` | V-8 |
| Envío cobrado | `freight_amount` del pedido de QEP igual al envío de Shopify; `total_difference` en 0 | V-9 |
| Envío gratis por descuento | Que entre `Free` y sin diferencia | V-9 |
| Uno por cada transportadora del checkout | Que el `title` sea el nombre que ve el comprador; si sale `carrier_unrecognized`, «Reconocer este nombre» en el detalle del pedido lo aprende | V-10 |
| Dirección colombiana con documento en el checkout | `provinceCode` (en especial Bogotá `DC`, San Andrés `SAP`, Guaviare `GUV`) y que el documento llegue en los atributos del pedido con la clave que configuraste (por defecto `Documento` y `Tipo de documento`) | V-4 |

**V-9: guarda los payloads reales como fixtures.** Los de `tests/Modules/Shopify/Modules.Shopify.UnitTests/Fixtures/rest`
son armados a mano (`"_source": "synthetic, replace per V-9"`). Del envío cobrado, del gratis por descuento y de la
recogida, exporta el cuerpo crudo:

```powershell
$sql = "SELECT payload FROM shopify.webhook_deliveries WHERE topic = 'orders/create' ORDER BY received_at DESC LIMIT 1"
docker exec postgres18 psql -U postgres -d <base> -At -c $sql | Out-File -Encoding utf8 shipping-charged.real.json
```

(en producción, la misma consulta en tu cliente de base). **Antes de commitearlos, cámbiales los datos personales**
(nombre, correo, teléfono, dirección, documento): son de una persona. Compara también si el REST del webhook trae las
líneas de envío retiradas con `is_removed` o las omite, y la forma de `discounted_price` y `tax_lines` frente a
`shippingLines` del GraphQL; si no cuadra, se ajustan los dos parsers.

**De la conciliación (V-5, V-9, tarea 15):** que la consulta `updated_at:>=` con `sortKey: UPDATED_AT` traiga lo
esperado, la forma real de `THROTTLED` y de `extensions.cost`, el costo de la consulta de un pedido, y si con la versión
`2026-10` el comprador viene en `defaultEmailAddress`/`defaultPhoneNumber` en vez de `email`/`phone` (deprecados; se ve
junto con V-3).

## 6. Prender `shopify` al tenant piloto

Los tenants nuevos nacen con el módulo; los que ya existían, no (D-S20). Para el piloto: consola de operador → el tenant →
Módulos → Shopify → activar (exige `orders`). Sin el módulo, el webhook guarda el espejo pero no importa nada, y al
prenderlo la conciliación siguiente importa lo acumulado (`ModuleOffStoresMirrorWithoutImportingThenReenableImports`).

## 7. Verificaciones contra Shopify (§16)

| # | Qué | Cómo se cierra | Si falla |
| --- | --- | --- | --- |
| V-1 | Que una app pública no embebida siga admitiendo el *authorization code grant* con redirección y token offline en `2026-10`, sin obligar a token exchange | Paso 4.3: primer enlace real desde QEP en la tienda de desarrollo | Flujo embebido con App Bridge: otro spec |
| V-2 | Que `webhookSubscriptionCreate` con el token offline registre los cinco topics y que las entregas se firmen con el client secret | Paso 4.5 (cinco ids) y el primer pedido real que entra sin 401 | Suscripción a nivel de app en la configuración |
| V-3 | Que sin la aprobación de datos protegidos el payload llegue sin PII y con ella completo | Un pedido antes y otro después de la aprobación (paso 2); comparar `payload` en `shopify.webhook_deliveries` | Nada en el código; es este paso |
| V-4 | La forma de `provinceCode` para Colombia y que los atributos traigan el documento | Pedido con dirección colombiana y documento en el checkout (paso 5) | Ajustar `ColombiaProvinceCodes` o las claves de la tienda |
| V-5 | Que `orders/updated` llegue en cada cambio de `financial_status` además de `orders/paid` | Pagar un pedido pendiente (paso 5) | La conciliación lo cubre con retraso |
| V-6 | Códigos de `shipping_lines` para recogida en el checkout colombiano | Pedido con recogida (paso 5) | Opción por tienda para marcar el método de recogida |
| V-7 | Que la App URL con `shop`, `hmac` y `timestamp` siga siendo cómo Shopify abre una app no embebida | Paso 4.4: instalar desde la tienda | Sólo el inicio A |
| V-8 | El `gateway` y el `financial_status` de un pedido contraentrega colombiano | Pedido contraentrega (paso 5) | Ajustar `ShopifyCashOnDeliveryGateways` |
| V-9 | La forma real de `shipping_lines` en el REST frente a `shippingLines` en GraphQL, y fixtures reales | Pedidos con envío cobrado, gratis por descuento y recogida, exportados como fixtures (paso 5) | Ajustar los dos parsers |
| V-10 | Que el `title` del envío llegue con el nombre del checkout del tenant | Un pedido por transportadora (paso 5) | Nada en el código: «Reconocer este nombre» |

## 8. Decisiones pendientes del owner

Cada una está implementada como dice la columna «Hoy»; si no la ratificas, el costo es lo que dice la última columna.

### Del spec (§15, D-S22 a D-S33, decididas por el escritor del plan)

| ID | Hoy | Si está mal, cuesta |
| --- | --- | --- |
| D-S22 | El tipo de documento se compara sin mayúsculas con `NIT`, `CC`, `CE`, `PASAPORTE`; uno desconocido cae al tipo por defecto de la tienda | Un motivo `document_type_unknown` o sinónimos por tienda |
| D-S23 | Idempotencia por `ExternalKey = "{shopId}:{shopifyOrderId}"` con índice único parcial | Una columna y un índice; sin ella, duplicados tras una caída |
| D-S24 | El cliente se anota en el espejo antes del pedido, en su propia transacción | Un incompleto huérfano si el pedido nunca entra |
| D-S25 | El `title` no reconocido queda en el pedido (`external_shipping_title`); recordar exige `tenancy.settings.update` | Mover la acción a la bandeja y quitar la columna |
| D-S26 | Un contraentrega nace `PaymentPending` y el pago se sincroniza justo después | Relajar `cash_on_delivery_pending_only` para el camino externo |
| D-S27 | Fuera de COP no se infla con la tasa; en COP el envío se redondea a pesos | Una regla por moneda |
| D-S28 | Con recogida no hay contraentrega | Un aviso `cash_on_delivery_on_pickup` |
| D-S29 | `ApiVersion` por defecto `2026-10` | Una clave de configuración |
| D-S30 | Varias líneas de envío: se suman, y la transportadora sale de la primera | Un aviso `multiple_shipping_lines` |
| D-S31 | Los avisos se sacan con «Marcar como revisado», no solos | Que cada corrección limpie su aviso por evento |
| D-S32 | Sin insignia «Creado desde Shopify» en la ficha del cliente | Un campo `origin` en el cliente |
| D-S33 | Dominio de la tienda hasta 64 caracteres | Ampliar la columna de rutas |

### De la implementación (rulings y líneas «owner» del ledger)

| Origen | Hoy | Si está mal, cuesta |
| --- | --- | --- |
| Ruling S5 | La frescura del `timestamp` del HMAC de la instalación no se valida; el `state` de un solo uso que vence a 15 minutos acota la repetición | Una comparación con ventana en `ShopifyHmac` |
| Tarea 7 | `GET /integrations/shopify/installs/{id}` lo lee cualquier autenticado que tenga el id (dominio, nombre, `initiatedTenantId`; el spec lo permite). El enlace sí revalida tenant y permiso | Exigir membresía en el tenant que inició, en `GetShopifyInstall` |
| Tarea 8 | Cuando la tienda ya está enlazada a otro tenant, su nombre va sólo en `detail`, en una frase en inglés | Un campo propio en el 422 (ver § 9) |
| Ruling S10 | TDD invertido de la tarea 8, aceptado sin rehacer (§ 10) | Rehacer las pruebas de integración de la tarea 8 |
| Tarea 11 | `shop/redact` no toca una conexión reinstalada y operativa; un fallo al encolar el webhook responde 500, no 503 | Una condición en el borrado; un status |
| Tarea 12 | `ShopifyCustomerNotCompletableException` se usa como control de flujo (Customers dice que los datos no alcanzan y el cliente nace incompleto) | Un resultado en el puerto en vez de la excepción |
| Tarea 12 | Una anulación que Quotations rechaza con algo que no es `already_invoiced` ni `already_cancelled` queda importada con el código en `last_error` | Otra transición o un motivo propio |
| Ruling S13 | `Cancelled` e `Ignored` son terminales: un pedido ignorado no se puede «des-ignorar» | Una transición nueva |
| Tarea 13 | Una línea con precio 0 entra (regalo legítimo); si en realidad faltaba el precio, sólo lo delata `total_difference` | Un motivo de espera |
| Ruling S19 | `refunded`, `partially_refunded` y `voided` no cambian nada ni dejan rastro | Un campo de auditoría |
| Ruling S20 | Segunda violación de TDD (tarea 13), aceptada sin rehacer (§ 10) | Rehacer dos pruebas |
| Ruling S23 | `already_cancelled` deja la fila `Cancelled` (si no, un guardado perdido la dejaría `Imported` para siempre); la reacción de pago y cancelación corre también con la conexión `Paused` o `NeedsAttention` | Una condición en la reacción |
| Tarea 15 | Los `Waiting` se reintentan los 200 más viejos por creación: si hay más de 200 que nunca entran, los nuevos esperan siempre | Ordenar por `last_attempt_at` |
| Tarea 15 | Un pedido nuevo de la conciliación con cabecera ilegible no tiene fila: sólo queda en el log (con tenant, conexión, pedido y motivo) | Una fila sin snapshot en la bandeja |
| Tarea 16 | Una cuenta de cobro en otra moneda al guardar la tienda responde `shopify.store.billing_account_not_found` | Un código propio (`billing_currency_mismatch`) |
| Tarea 16 | `importedToday` del resumen cuenta también las filas que después se cancelaron | Un filtro por estado |
| Tarea 16 | Una clasificación por defecto inactiva se acepta (como en Customers); los clientes nacen completos con ella | Una validación en el `PUT` |
| Tarea 16 | Un `PUT` de la tienda sin cambios audita `[]` (Tenancy no audita en ese caso) | Saltar la auditoría sin cambios |
| Rulings S27 y S29 | El `PUT` de la tienda audita `shopify.store.updated` con los **nombres** de los campos que cambiaron, sin valores | Renombrar la acción |
| Tarea 17 | Un pedido de prueba (`test = true`) se ignora sin consultar la clave externa; un cancelado sí la consulta | Consultar también en los de prueba |
| Tarea 17 | En `Production`, un `ClientId` o `ClientSecret` que empieza por `#{` cuenta como ausente | Ninguno, salvo que Shopify emitiera un secreto con ese prefijo |

### Códigos por ratificar (no están en §7.1 del spec)

| Código | Dónde | Hoy |
| --- | --- | --- |
| `shopify.store.not_operational` | HTTP 422 | Sincronizar o reintentar con la conexión que no está operativa, o Shopify rechazó las credenciales (401/402) durante la sincronización |
| `shopify.store.sync_unavailable` | HTTP 503 | Shopify no respondió (red, 5xx, 403/`ACCESS_DENIED` por ruling S24) durante «Sincronizar ahora» |
| `shopify.webhook.signature_invalid` | HTTP 401 | Firma del webhook ausente o inválida (el spec sólo dice «401»; análogo a `messaging.webhook.signature_invalid`) |
| `unsupported_topic` | `ignored_reason` de la entrega | Un topic que el worker no atiende |
| `unknown_shop` | `ignored_reason` de la entrega | Ninguna ruta para ese dominio (el spec lo nombra en §5.2; queda para ratificar con los demás) |
| `missing_headers`, `header_too_long`, `invalid_json`, `shop_domain_mismatch` | Descarte del webhook: 200 y aviso en el log, sin fila | Cabeceras faltantes o demasiado largas, cuerpo que `jsonb` no acepta, dominio del cuerpo distinto del de la cabecera |
| `invalid_payload` (prefijo) | `last_error` de la entrega o de la conciliación | Fallo permanente del parser o de la cabecera, con el campo: `invalid_payload:<campo>` |
| `shopify.compliance.customers_redact` | Acción de auditoría | El spec sólo nombra `shopify.compliance.data_request` |
| `shopify.store.updated` | Acción de auditoría | Rulings S27 y S29 |

## 9. Contrato para el frontend

Lo que el backend hace y la pantalla tiene que saber. Va también al plan del frontend
(`qep-frontend/docs/superpowers/plans/2026-10-18-shopify-frontend.md`).

- **`install/complete` lleva `host`** (ruling S7): Shopify lo firma en el callback y el HMAC no cuadra sin él. Reenvíalo
  tal como llegó en la query. Y lleva `X-Qep-Client: web`: no está exento de CSRF (ruling S8), sólo el inicio B lo está.
- **`integrations.shopify.webhooks_not_registered`** (422 al enlazar): la conexión quedó creada pero `NeedsAttention`.
  Mensaje propio, con «Probar conexión»; no es un fallo de credenciales.
- **El nombre del otro tenant** en `shop_already_linked` va sólo en `detail`, en una frase en inglés. No lo parsees: si
  la pantalla lo necesita, pídelo como campo (§ 8, tarea 8). `install/complete` sí trae `linkedTenant { tenantId, name }`.
- **`last_error`** puede traer varios códigos unidos con `"; "` (anulación y pago rechazados en el mismo aviso).
  Sepáralos antes de traducir.
- **`detail: null`** en los avisos `shipping_address_unresolved` y `total_difference`: la dirección cruda va en las
  notas del pedido y la diferencia en `totalDifference` de la fila.
- **`{"skus":[null]}`** en `product_unmatched`: una línea sin SKU entra a la lista como `null`. Muéstrala como «línea sin
  SKU».
- **`withWarnings=true` sin `state`** no filtra por estado (ruling S28), para que la lista cierre con
  `summary.withWarnings`. Con `state`, se respeta.
- **La dirección cruda** que no quedó como parte de envío va en las notas del pedido con el rótulo
  `Dirección de envío de Shopify: ` y el país como código (`CO`).
- **«Reconocer este nombre»**: `shipping.externalShippingTitle` se conserva después de reconocer (ruling S2); esconde la
  acción cuando `shipping.carrier` no es `null`. Un título de más de 120 caracteres no se puede recordar
  (422 `quotations.carrier.external_name_too_long`); hace falta un texto para ese caso.
- **Códigos nuevos**: `shopify.store.not_operational` (422) y `shopify.store.sync_unavailable` (503) en «Sincronizar
  ahora» y «Reintentar» (§ 8, por ratificar), además de `shopify.store.sync_in_progress` (409) del spec.
- **El `PUT` de la tienda** responde 428 sin `If-Match` antes de validar el cuerpo.

## 10. Violaciones de TDD aceptadas

La regla dura es RED antes que GREEN, con la falla literal. Estas no la cumplieron y se aceptaron sin rehacer:

- **Tarea 8 (ruling S10):** las pruebas de integración se escribieron después del código y el RED se armó desactivando
  piezas.
- **Tarea 13 (ruling S20):** el RED del e2e de S18 y el de la semilla de Geography salieron de una mutación, no de
  código ausente.
- **Tarea 14:** el e2e `WorkerCancelledInShopifyCancelsTheImportedOrder` se escribió después del código (su RED vino de
  `ArchitectureTests`), y dos pruebas de Quotations del arrastre de la tarea 2 nacieron verdes.
- **Tarea 15:** la aserción de `CompositionRootTests.cs:409-412` nació verde.
- **Tarea 16:** el handler de la lista de tiendas se escribió antes del RED y se apartó para provocarlo; dos aserciones
  se corrigieron en GREEN sin RED propio.
- **Tarea 17:** las doce pruebas del flujo y la de fuga de secretos nacieron verdes porque las tareas 1 a 16 ya cubrían
  ese comportamiento (no se fabricó un RED); la de la fila nueva cancelada con el pedido en QEP
  (`ReimportAfterMirrorRowDeletedCancelledCancelsTheExistingOrder`) se escribió después del arreglo, cuyo RED fue la
  prueba unitaria `CancelledNewRowWithTheOrderAlreadyInQepCancelsIt`.
