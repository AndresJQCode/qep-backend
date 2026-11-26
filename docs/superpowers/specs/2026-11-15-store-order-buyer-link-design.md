# Enlace de pedidos de la tienda con la cuenta del comprador por correo

Fecha: 2026-11-15. Repos: `qep-backend` (este spec) y `qep-frontend` (la parte de la consola, §6, lleva su propio spec).

## 1. Por qué

Un pedido de la tienda pública aparece en «Mis pedidos» del comprador sólo si `quotations.orders.buyer_account_id` apunta a su
cuenta. Hoy eso pasa en dos casos:

- el checkout trae una sesión válida (`SubmitOrderRequest.ResolveBuyerAccountAsync`);
- el checkout pidió «crear cuenta» y la cuenta es nueva o está sin verificar (`BuyerSignupRequestedWorker.LinkOrderAsync`).

Cualquier otro pedido queda en `NULL` para siempre. Caso real del 2026-11-15 (`PED-2026-0005`): el comprador tenía cuenta
verificada desde el 9 de noviembre, compró sin sesión y marcó «crear cuenta». `BuyerSignup.SignUpAsync` devolvió `null` (cuenta
verificada → aviso «ya tienes cuenta») y el pedido no se enlazó. El asesor lo ve en el cliente; el comprador no lo ve en su cuenta y
no hay forma de enlazarlo.

**Decisión del owner (2026-11-15):** el pedido se enlaza a la cuenta **verificada** cuyo correo coincide con el correo de
facturación del pedido. Si el comprador escribió mal su correo es su error, no de QEP, y se corrige con un asesor (§6).

## 2. Qué cambia y qué no

- **Cliente y cuenta siguen separados.** El cliente (`customers.customers`, por tenant) se sigue eligiendo por documento; la cuenta
  (`buyers.accounts`, global) es el acceso a la tienda. Nada de este spec los une: el pedido sigue siendo lo único que lleva los dos.
- **El correo del cliente no se vuelve único** (`IX_customers_tenant_email` sigue sin ser único).
- La regla de privacidad de `BuyerSignup` para cuentas verificadas (no enlazar el pedido que pidió el alta, mandar «ya tienes
  cuenta») **se reemplaza** por la de este spec: el pedido se enlaza en el checkout por la regla 3.1, antes de que el worker corra.

### Objetivos

1. Checkout sin sesión → enlazar a la cuenta verificada del correo de facturación (§3.1).
2. Inicio de sesión en la tienda X → enlazar los pedidos de ese tenant sin cuenta cuyo correo coincide (§3.2).
3. El asesor ve la cuenta del pedido, la quita o la cambia (§4, §6).

### Fuera de alcance

- Enlazar al verificar la cuenta: la verificación no tiene tenant y no abre sesión; el siguiente inicio de sesión cubre ese caso
  (opción 1 elegida por el owner, §5).
- Unir cuenta y cliente con una columna.
- Mostrar las ventas POS en el detalle del cliente (es otra petición).

### Anti-objetivos

- Que una acción del asesor la deshaga el siguiente inicio de sesión del comprador (§3.3).
- Enlazar a una cuenta sin verificar: no prueba que el correo sea de quien la creó.
- Que el enlace haga fallar un checkout o un inicio de sesión.

## 3. Reglas

### 3.1 Checkout

En `SubmitOrderRequest`, **antes de crear el pedido** y sólo si la sesión no resolvió cuenta:

- se normaliza el correo de facturación con la regla de la cuenta (`StoreBuyerSignup.TryNormalizeEmail` / `BuyerEmail`);
- se busca una cuenta **verificada** con ese correo (puerto nuevo en `IStoreBuyerAccounts`, por ejemplo
  `FindVerifiedAccountIdAsync(email)`, adaptador en `StoreBuyerAccounts`);
- si existe, va en `StoreOrderDraft.BuyerAccountId`, igual que la de la sesión.

La sesión gana siempre: si hay sesión válida, su cuenta va al pedido aunque el correo de facturación sea otro (comportamiento de
hoy, sin cambio). Una falla de la búsqueda se registra (sin correo) y el pedido sigue sin cuenta, como `ResolveBuyerAccountAsync`.

El alta diferida no cambia: con «crear cuenta» y cuenta nueva o sin verificar, el worker sigue enlazando por `LinkAsync`, que sólo
escribe si el pedido no tiene cuenta.

### 3.2 Inicio de sesión

En `LoginBuyerHandler`, **después** del commit de la sesión y fuera de su transacción:

- se enlazan los pedidos del tenant de la tienda (`BuyerStorefront.TenantId`) que cumplan **todo**:
  - `buyer_account_id IS NULL`;
  - `source = 'Store'`;
  - la marca de §3.3 en falso;
  - el correo de la parte de facturación (`order_parties`, rol facturación), recortado y en minúsculas, igual al correo de la
    cuenta.
- puerto nuevo en Buyers (ampliar `IBuyerOrderLinker` con `LinkByEmailAsync(tenantId, accountId, email)`), adaptador en
  Bootstrapper, implementación en Quotations (`IBuyerOrderGateway`).
- el enlace no sube `Version` (como `Order.LinkBuyerAccount`), y se escribe con una sentencia que repite las condiciones en el
  `WHERE`, para no pisar una escritura concurrente del asesor (§3.3).
- una falla se registra (con el id de la cuenta, nunca el correo) y el inicio de sesión responde igual.

### 3.3 La decisión del asesor manda

Columna nueva `quotations.orders.buyer_account_set_by_member` (bool, `NOT NULL DEFAULT false`, migración aditiva). La pone en
`true` toda acción del asesor sobre la cuenta del pedido (quitarla o cambiarla, §4). Con `true`, ni el checkout (no aplica: el
pedido ya existe) ni el inicio de sesión (§3.2) ni el alta diferida (`LinkAsync`) vuelven a tocar la cuenta del pedido.

Sin esa marca, quitar la cuenta a un pedido con correo mal escrito no serviría: el dueño de ese correo lo recuperaría en su
siguiente inicio de sesión.

`Order.LinkBuyerAccount` (el único camino automático: alta diferida y §3.2) devuelve `false` con la marca en `true`, así que
`LinkAsync` la respeta sin cambiar su firma. `BuyerSignup.SignUpAsync` no cambia: con cuenta verificada sigue devolviendo `null` y
mandando «ya tienes cuenta»; el pedido ya quedó enlazado en el checkout (§3.1).

### 3.4 Pedidos anteriores al despliegue

Se enlazan, a propósito: la migración deja la marca en `false` en todas las filas, así que el primer inicio de sesión de cada
comprador en cada tienda enlaza sus pedidos históricos sin cuenta (es lo que arregla `PED-2026-0005`). No hay pedidos «quitados por
un asesor» antes de este spec: la acción no existía.

Un correo de facturación `NULL` o vacío nunca coincide; la comparación es `lower(trim(email)) = @email` y tolera el `NULL`.

## 4. Consola: ver, quitar y cambiar la cuenta

- `OrderDto` gana `buyerAccount`: `{ id, email }` o `null`. El correo lo resuelve un puerto de Quotations hacia Buyers (adaptador en
  Bootstrapper; Quotations no ve Buyers). Una cuenta borrada sale `null` aunque quede el id (el borrado de la cuenta no limpia
  pedidos hoy; el builder confirma qué hace `DeleteBuyerAccount` con `buyer_account_id`).
- `PUT /api/v1/tenants/{tenantId}/orders/{orderId}/buyer-account` con `{ "email": "…" }`: enlaza a la cuenta verificada con ese
  correo. Cuenta inexistente o sin verificar → 422 `order.buyer_account.not_found` (un solo código para los dos: el asesor no
  necesita distinguirlos). La búsqueda es sobre todas las cuentas (son globales), no sólo las que ya compraron en el tenant:
  **decisión del owner (2026-11-15)**, aceptando que un asesor con `quotations.order.manage` puede averiguar si un correo tiene
  cuenta verificada; cada intento exitoso queda auditado. Limitarla dejaría sin salida al comprador nuevo que se equivocó de correo
  en su primera compra.
- `DELETE …/buyer-account`: la quita.
- Los dos: permiso `quotations.order.manage`, revalidación de tenant y permiso en el handler (403, nunca 404), `If-Match` con la
  versión del pedido, suben `Version`, marcan §3.3, auditoría (acción nueva, por ejemplo `quotation.order.buyer_account_changed`, con el id
  anterior y el nuevo, nunca correos) y responden el `OrderDto` completo.
- **Historial del pedido (owner, 2026-11-15):** cada cambio que sube la versión deja además una entrada `BuyerAccountChanged`
  (valor nuevo al final de `OrderHistoryEventType`) en el mismo guardado, con el asesor como autor. Su texto nombra la cuenta
  anterior y la nueva **con sus correos** (a diferencia de la auditoría): lo lee el mismo equipo que ya ve el correo en el detalle, y
  la foto mantiene la entrada legible si la cuenta se borra después (una anterior ya borrada se nombra «una cuenta ya eliminada»).
  Sólo la decisión del asesor la escribe: el checkout, el inicio de sesión y el alta diferida no tocan el historial.
- Sólo pedidos con `source = 'Store'`; otro origen → 422 `order.order.source_invalid` (código existente).
- **Casos de borde (decisión de diseño):** `PUT` con la misma cuenta que ya tiene y la marca ya en `true` → 200 sin cambios (no
  sube versión ni audita). Cualquier otro `PUT` o `DELETE`, incluido un `DELETE` sobre un pedido sin cuenta, sube versión, pone la
  marca y audita: «quitar» sobre un pedido vacío significa «no lo enlaces solo», y esa decisión vale.
- `buyerAccount` va sólo en el `OrderDto` del detalle (`GET …/orders/{id}` y las respuestas de escritura). Si el listado comparte el
  record, el builder lo separa o lo deja `null` en las filas: la lista no hace la búsqueda cruzada.

## 5. Enfoques considerados

1. **Enlazar al iniciar sesión, por tenant (elegido).** Siempre hay tenant, cubre los dos caminos de verificación (después de
   verificar siempre se inicia sesión) y no necesita worker. Si compró en dos tiendas, cada una enlaza al entrar en ella.
2. **Al verificar, con evento y worker.** Enlaza en todos los tenants de una vez, pero exige una consulta sin tenant y cubrir el
   enlace de verificación y la recuperación de contraseña (`BuyerPasswordRecovery` también verifica).
3. **Al listar «Mis pedidos».** Siempre al día, pero un GET que escribe.

## 6. Frontend (spec aparte en `qep-frontend`)

En el detalle del pedido de origen tienda: la cuenta de tienda (correo o «Sin cuenta»), y con `quotations.order.manage`, «Cambiar
cuenta» (pide el correo) y «Quitar cuenta» (con confirmación).

## 7. Pruebas mínimas

- Checkout sin sesión con cuenta verificada del correo → pedido enlazado; con cuenta sin verificar → no; con sesión de otra cuenta →
  la de la sesión.
- Caso `PED-2026-0005`: «crear cuenta» con cuenta verificada → enlazado y aviso «ya tienes cuenta».
- Inicio de sesión enlaza sólo pedidos del tenant de la tienda, de origen `Store`, sin cuenta, sin marca y con correo igual sin
  distinguir mayúsculas ni espacios; no toca los de otro tenant.
- Quitar la cuenta y volver a iniciar sesión → el pedido sigue sin cuenta.
- `PUT`/`DELETE`: 403 sin permiso u otro tenant, 412 con versión vieja, 422 con cuenta sin verificar u origen no tienda, auditoría.

## 8. Despliegue

Migración aditiva (una columna con default): compatible con pods viejos. Sin cambios de contrato con otros módulos fuera de los
puertos nuevos.
