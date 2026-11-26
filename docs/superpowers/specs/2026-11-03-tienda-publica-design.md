# Tienda pública por tenant — diseño

Fecha: 2026-11-03. Estado: borrador para aprobación del owner. Cruza tres piezas: `qep-backend`,
`qep-frontend` (backoffice) y una app nueva en Astro (`qep-storefront`, nombre provisional).

Las marcas **[decisión del diseñador]** son decisiones tomadas al escribir este documento, no del
owner; están para revisarse.

## 1. Intención

Que cada tenant pueda publicar una tienda en línea con su catálogo, donde un visitante sin cuenta
arme un carrito y envíe una **solicitud de pedido**. La venta no se cierra en la tienda: la cierra un
asesor por WhatsApp. La tienda es una vitrina que genera oportunidades de venta dentro de QEP, no un
e-commerce con pago.

## 2. Objetivos, no objetivos y anti-objetivos

**Objetivos**

- Tienda pública por tenant en `{slug}.lulocrm.com`, con su marca básica.
- Catálogo navegable por categorías y búsqueda, con variantes y precios.
- Carrito y envío de una solicitud de pedido con los datos de contacto del visitante.
- Bandeja común «Desde la tienda» en el backoffice, donde un asesor toma la solicitud, la convierte
  en pedido y contacta al cliente por WhatsApp.

**No objetivos (MVP)**

- Pago en línea.
- Cuentas de visitante, inicio de sesión y seguimiento del pedido por el visitante.
- Dominios propios del tenant (la base los permite; ver §5).
- Editor de páginas, plantillas alternativas, tipografías propias.
- Notificaciones push de solicitudes nuevas.
- Mostrar existencias: el inventario está fuera del alcance del proyecto (`product.ts:13-15`).

**Anti-objetivos (sería un fracaso aunque funcione)**

- Que la tienda debilite la seguridad del backoffice: abrir la CORS de la API, compartir la cookie de
  sesión o ejecutar código del tenant.
- Que el spam llene la tabla de pedidos o consuma números de pedido.
- Que el navegador calcule precios, escalas o totales (regla BFF del proyecto).
- Que un producto aparezca en la tienda sin que el tenant lo haya decidido.

## 3. Dominios y aislamiento

| Host | Qué sirve |
| --- | --- |
| `qep.qcode.co` | backoffice (SPA existente) |
| `qep-api.qcode.co` | API |
| `lulocrm.com` | landing comercial |
| `{slug}.lulocrm.com` | tienda de cada tenant (Astro) |

- Backoffice y tiendas quedan en **dominios registrables distintos**: la cookie de sesión
  (`SameSite=Lax`, sin `Domain`, `SessionCookieWriter.cs:24-31`) nunca viaja desde una tienda.
  **Regla**: la cookie nunca se emite con `Domain`.
- **El navegador del visitante nunca llama a `qep-api`.** Astro llama al backend desde su servidor
  (SSR para leer; endpoint propio de Astro para enviar la solicitud). La CORS de la API sigue
  admitiendo sólo `qep.qcode.co`.
- **Slugs reservados** (validados por el backend): `www`, `app`, `api`, `admin`, `mail`, `smtp`,
  `ftp`, `static`, `cdn`, `docs`, `status`, `blog`, `help`, `soporte`, `tienda`, `shop`, y los hosts
  de desarrollo que ya viven bajo `lulocrm.com`: `qep`, `qep-api`, `tunnel`. Los registros DNS
  explícitos le ganan al comodín, así que esos hosts siguen funcionando.
- DNS: comodín `*.lulocrm.com` hacia el proyecto Astro en Vercel. **Verificar antes de construir**
  si Vercel exige sus nameservers para un dominio comodín; si es así, `lulocrm.com` pasa a DNS de
  Vercel y hay que recrear allí los registros existentes.

## 4. Experiencia

### 4.1 Visitante (Astro)

- **Portada**: logo, nombre, banner con texto de bienvenida, categorías destacadas, productos.
- **Categoría / búsqueda**: lista paginada de productos.
- **Producto**: galería, descripción, selector de variantes y precio de la variante elegida. Con
  escalas activadas, la tabla de tramos («desde 12 und: $X»). La restricción de la escala (múltiplo
  o empaque) **sólo decide si aplica el descuento, nunca bloquea la cantidad**
  (`quote-scale-quantity.ts:12-27`): la tienda muestra una pista y el `scaleApplied` del backend.
- **Moneda** (owner, 2026-11-03): con dos monedas o más, un selector «COP / EUR» en el encabezado;
  con una, no se muestra. Arranca en la principal y la elección se recuerda en el navegador. Un
  producto sin precio en la moneda elegida no se muestra.
- **Carrito**: líneas por variante y total calculado por el backend, en la moneda elegida.
  Persistido en `localStorage` del subdominio (aislado por tienda de forma natural). Al cambiar de
  moneda se vuelve a preciar; una línea sin precio en la nueva moneda queda marcada como no
  disponible, no se borra sola.
- **Enviar solicitud** (owner, 2026-11-03): un formulario de **facturación** y uno de **envío**.
  - Facturación: nombre, tipo y número de documento, teléfono con indicativo (WhatsApp), país (de
    los países de venta), dirección y ciudad (departamento y ciudad de DIVIPOLA en Colombia, ciudad
    en texto fuera de ella) y **email**, todos obligatorios. El email es obligatorio porque una
    parte del pedido no está completa sin él (`OrderParty.cs:45-51`; owner, 2026-11-03). El envío pide lo mismo de dirección.
  - Casilla «Usar los mismos datos para el envío», **marcada por defecto**. Desmarcada, pide
    dirección, departamento, ciudad y teléfono de envío (obligatorios) y nombre de quien recibe
    (opcional; vacío = el de facturación) **[decisión del diseñador]**.
  - Departamento y ciudad salen de `geography` por una ruta pública nueva, porque la libreta del
    cliente guarda la ciudad como id.
  - Nota libre (opcional), consentimiento de tratamiento de datos (enlace a la política del tenant)
    y Cloudflare Turnstile.
- **Confirmación**: «Recibimos tu pedido. Te escribiremos por WhatsApp.» con un número de solicitud.
- Pie: WhatsApp, dirección, horario y redes sociales del tenant.
- Tienda despublicada o slug inexistente: página 404 propia, sin revelar si el tenant existe.

### 4.2 Backoffice — configuración (`/settings/store`)

Detrás de un módulo nuevo `store` (`tenant-modules`) y del permiso `store.settings.manage`,
asignado **sólo al rol `admin`** (owner, 2026-11-03); otro rol lo recibe desde el editor de roles.

- Publicar o despublicar la tienda; slug (validado contra reservados y unicidad) y enlace a la
  tienda.
- Marca: nombre de la tienda (por defecto, el del tenant), logo (el del tenant, no se vuelve a
  subir), banner (imagen y texto) y **colores** (owner, 2026-11-03):
  - **Color principal libre** (hex), con seis atajos: Azul `#1D4ED8`, Verde `#15803D`, Rojo
    `#B91C1C`, Naranja `#C2410C`, Morado `#6D28D9`, Grafito `#1F2937` (mockup
    `paleta-tienda.html`).
  - **Propuesta automática, editable**: el sistema propone el color del texto sobre el principal
    (blanco o casi negro, el de mayor contraste) y el color de enlaces y precios sobre fondo blanco
    (el principal, oscurecido en el mismo tono hasta 4,5:1 si no llega). El admin puede cambiar los
    dos; vacíos = automáticos.
  - Si una combinación queda bajo 4,5:1 (WCAG AA), la pantalla avisa y sugiere el automático, pero
    **deja guardar**: la decisión es del admin.
  - Lo calcula el **backend**, que entrega a la tienda los colores ya resueltos; la vista previa de
    `/settings/store` y la tienda pintan lo mismo.
- Contacto: WhatsApp, dirección, horario y redes.
- **Política de tratamiento de datos**: URL obligatoria para publicar (hoy no hay una entidad de
  política en el backend). La «versión» que guarda el consentimiento es la URL vigente más la fecha
  en que se fijó **[decisión del diseñador]**.
- Categorías destacadas.
- **Monedas de la tienda** (owner, 2026-11-03): una **lista**, no un campo. Cada moneda va con
  **su cuenta de facturación**, y una de ellas es la **principal** (la que ve primero el
  visitante). Una tienda de una sola moneda es una lista de un elemento.
  - Se ofrecen sólo las monedas en las que hay productos con precio (la misma fuente de
    `GET /catalog/currencies-in-use`), nunca una lista fija.
  - La cuenta de cada moneda tiene que estar en esa moneda, porque el pedido cobra en la moneda de
    su cuenta (`Order.cs:96`). El selector de cuenta muestra sólo las de esa moneda; sin ninguna, esa
    moneda no se puede agregar. El backend rechaza la diferencia con 422
    `store.settings.currency_mismatch`.
  - Publicar exige al menos una moneda con su cuenta (lo exige `Order.billingAccount`, igual que
    `ShopifyStoreSettings`).
  - No hay tasas de cambio: cada moneda es la lista de precios que el producto ya tiene
    (`prices: Record<string, number>`). Nunca se convierte ni se suma entre monedas.
- **Países de venta** (owner, 2026-11-03): el admin elige a qué países vende la tienda, y el
  formulario de facturación y envío sólo admite esos países; el backend rechaza otro con 422. Se
  eligen del mismo catálogo de países del cliente (`features/customers/utils/countries.ts`, código
  ISO-3166-1 alpha-2). Con un solo país, el formulario no muestra el selector de país.
- **Ciudad fuera de Colombia** (owner, 2026-11-03, opción B): igual que el cliente
  (`customer-form.schema.ts:163-178`), con Colombia se eligen departamento y ciudad de DIVIPOLA; con
  otro país la ciudad es **texto**. No se carga geografía de otros países. Hoy la libreta
  (`CustomerAddress.CityId`, `Guid` obligatorio, `CustomerAddress.cs:60`) y las partes del pedido
  sólo guardan ids de DIVIPOLA, así que **las dos ganan país y ciudad en texto** para direcciones
  fuera de Colombia: cambio en los módulos Customers y Quotations, con migración, fuera del módulo de
  la tienda. Una dirección extranjera de la libreta **se puede editar** desde la ficha del cliente,
  con la misma regla de país y ciudad en texto (owner, 2026-11-03).
- **Modo de precios**: «Sólo detal» (precio base de la variante) o «Con escalas por cantidad». Uno
  para toda la tienda, no por moneda **[decisión del diseñador]**.

### 4.3 Backoffice — visibilidad por producto

Cada producto tiene «Visible en la tienda», **apagado por defecto**. Se cambia en el formulario del
producto y en lote desde la lista del catálogo. Sólo se publican variantes activas de productos
activos.

### 4.4 Backoffice — bandeja «Desde la tienda» (`/orders/store`)

Aprobada con mockup (`.superpowers/brainstorm/…/bandeja-tienda.html`). Sigue el patrón de
«Desde Shopify» (`shopify-inbox-page.tsx`):

- En el encabezado de Pedidos, el enlace «Desde la tienda» con contador de solicitudes sin tomar.
- Vistas: «Sin tomar», «Tomados por mí», «Todos». «Recibidos hoy» a la derecha.
- Columnas: recibido, cliente (nombre, teléfono, «cliente nuevo» o «ya es cliente»), productos
  (líneas, unidades, «con escala aplicada»), total en la moneda de esa solicitud, estado, acción.
  La columna se llama «Total», sin moneda, y nunca se suman totales de monedas distintas.
- Acciones: **Tomar** (sin tomar); **Escribir por WhatsApp** y **Abrir pedido** (tomada);
  **Descartar** con motivo (spam, duplicado, otro).
- **Escribir por WhatsApp** (decisión del owner) abre «Nueva conversación» de Mensajería con el
  teléfono de la solicitud precargado: el cliente nunca escribió, así que el primer mensaje es una
  plantilla aprobada (el flujo existente de `TemplateComposer`). Depende del módulo de mensajería, de
  un canal de WhatsApp conectado y de que el asesor tenga permiso para iniciar conversaciones. Sin
  eso, el botón no se ofrece y la fila muestra el teléfono para copiar **[decisión del diseñador]**.
  Se abre con el **cliente del pedido**, no con un teléfono (`StartConversationInput.customerId`).
  Siempre envía una plantilla; el backend la escribe en la conversación que el cliente ya tenga en
  ese número, o abre una asignada a quien la inicia (`messaging.api.ts:369-370`), así que nunca
  duplica. Abrir la conversación existente sin plantilla queda fuera del MVP (owner, 2026-11-03).
- **Ubicación del botón** (decisión del owner): en la bandeja, en la columna de acción de la fila
  convertida, junto a «Abrir pedido»; y en el **detalle del pedido**, junto a las acciones del
  encabezado, sólo cuando `source = 'Store'`. Las mismas reglas de disponibilidad en los dos
  lugares. El teléfono sale de la solicitud de origen, así que el detalle del pedido necesita
  conocerla (por ejemplo, un `storeRequest` con el teléfono en `OrderDetail`, nulo para otros
  orígenes; el contrato exacto queda al builder).
- El WhatsApp del pie de la tienda es un enlace `wa.me` al número del tenant: ahí escribe el
  visitante, y eso abre la ventana de 24 h para responderle sin plantilla.
- Ver y tomar: `quotations.order.manage`, el permiso que ya exige crear pedidos
  **[decisión del diseñador]**.
- Sin push: el contador se actualiza al refrescar, como el resto de listados.

## 5. Modelo

### 5.1 Solicitud de pedido, no pedido

Un `Order` nace `Pending` y exige `advisorId`, `clientId` y `billingAccount` (`order.ts:45-57`); no
existe un estado «borrador». Por eso la tienda **no crea un `Order`**: crea una **solicitud**
(`store_order_requests`) en el módulo de la tienda, con:

- datos de facturación y, si son distintos, de envío, tal como los escribió el visitante;
- líneas: `variantId`, cantidad, y el precio unitario, el descuento de escala y el nombre visible de
  la variante **congelados** al enviar;
- moneda, total mostrado, nota, consentimiento (fecha, IP, versión de la política);
- estado: `New → Converted` o `New → Discarded`. No hay un estado «tomada sin convertir»: tomar es
  convertir (§5.2). Se guarda quién la convirtió o descartó, cuándo y, al convertir, el `orderId`.
  «Tomados por mí» lista las `Converted` por quien consulta. **Descartar sólo aplica a `New`**: una
  convertida ya es un pedido y se cancela como cualquier pedido.

### 5.2 Tomar = convertir

«Tomar» convierte la solicitud en un `Order` con concurrencia optimista: si otro asesor la tomó
antes, responde 409 y la bandeja relee. Solicitud y pedido viven en módulos con unidades de trabajo
distintas, así que no hay una transacción que los cubra: se sigue el patrón de la importación de
Shopify (bloqueo de la solicitud, pedido idempotente por `ExternalKey`, y recién después la
solicitud pasa a `Converted`). Un reintento después de una falla a mitad de camino encuentra el
pedido ya creado y no lo duplica.

- `source = 'Store'` (nuevo valor de `OrderSource`, con su etiqueta «Tienda»).
- `advisorId` = quien la toma.
- `billingAccount` = la configurada en la tienda **para la moneda de la solicitud**. Si esa moneda
  ya no está en la tienda, «Tomar» responde 422 y el asesor lo resuelve en la configuración
  **[decisión del diseñador]**.
- **Cliente**: si el teléfono normalizado o el documento coinciden con **un solo** cliente del
  tenant, el diálogo lo propone ya elegido; si coinciden varios (o el teléfono con uno y el documento
  con otro), el diálogo los lista y el asesor elige uno o crea uno nuevo; nunca se elige solo en un
  caso ambiguo **[decisión del diseñador]**. Si no coincide ninguno, el cliente se crea con los
  datos de facturación de la solicitud (documento incluido, ya obligatorio en la tienda); el diálogo
  sólo pide lo que la tienda no puede saber (por ejemplo, la clasificación) **[decisión del
  diseñador]**. Nunca se crean clientes desde el endpoint público.
- **Libreta de direcciones** (owner, 2026-11-03): con el cliente elegido o creado, cada dirección de
  la solicitud (la de facturación y, si es distinta, la de envío) se busca en su libreta
  (`QuoteClientAddress`: nombre, dirección, teléfono, departamento, ciudad). Si ya está, no se crea;
  si no, se agrega. Cuenta como la misma si coinciden dirección, ciudad y teléfono, sin distinguir
  mayúsculas, espacios repetidos ni formato del teléfono **[decisión del diseñador]**. Los datos
  maestros de un cliente que ya existía no se pisan.
- **Cliente incompleto** (owner, 2026-11-03): si el elegido está incompleto (por ejemplo, creado
  desde WhatsApp), «Tomar» lo **completa** con los datos de facturación de la solicitud: sólo llena
  lo que le falta y nunca pisa lo que ya tiene, como `seedCustomerDocument`. Recién completo recibe
  las direcciones en su libreta, porque el backend no edita un incompleto por partes
  (`Customer.cs:700-707`).
- **Partes del pedido**: la facturación de la solicitud va a la parte `Billing` y el envío a la
  parte `Shipping`; con la casilla marcada, el envío usa la misma dirección.
- Modo de precios → pedido: «Sólo detal» = `isRetail: true`; «Con escalas» = `isRetail: false` y
  `globalScaleFloor: null`. El carrito (`cart/quote`) usa **el mismo motor** de precios de los
  pedidos, agrupación entre líneas (`allowGrouping`) incluida, para que lo que ve el visitante y lo
  que da el pedido coincidan **[decisión del diseñador]**. Carrito y pedido redondean con **la
  regla del tenant**, como los pedidos `Quotation` y `Manual`, no con el default de la moneda que
  usa Shopify: el precio sale del catálogo de QEP, no de una plataforma externa (owner, 2026-11-03).
- Líneas con las variantes y cantidades de la solicitud. **El pedido se precia con el motor normal**;
  si el total difiere del mostrado al visitante, el pedido lo indica (como `totalDifference` en
  Shopify) para que el asesor lo explique. **La tienda no calcula retención ni excedente de IVA**
  (owner, 2026-11-03): el carrito se precia para un comprador anónimo, y esas reglas recién aplican
  al convertir, con el cliente real. Es la causa esperada de una diferencia.
- Entrega: a la dirección de envío de la solicitud, no recogida en tienda (owner, 2026-11-03). El
  asesor la cambia en el editor.

Después de convertir, el pedido sigue el flujo de siempre: el asesor lo ajusta en el editor existente
y lo reenvía al cliente por WhatsApp para su aprobación.

### 5.3 Resolución de tienda

Tabla `store_domains` (hostname → tienda). El MVP sólo inserta `{slug}.lulocrm.com`; un dominio
propio será otra fila más el alta en Vercel, sin tocar la app.

## 6. Contrato público (servidor de Astro → backend)

Rutas anónimas bajo `/api/v1/public/stores/{host}/…` **[decisión del diseñador]**, rate-limited por
IP del visitante (Astro reenvía la IP original en un header firmado o confiable; detalle para el
builder) y cacheables:

- `GET …/` — marca, contacto, monedas (con la principal), modo de precios, categorías destacadas y
  el **formato regional del tenant** (separadores, patrón de fecha, zona horaria), el mismo que
  viaja en `/auth/me` (owner, 2026-11-03). La tienda nunca escribe `'es-CO'` a mano.
- `GET …/categories?currency=` — árbol con conteo de productos visibles con precio en esa moneda.
- `GET …/products?currency=&category=&q=&page=` — lista paginada.
- `GET …/products/{id}?currency=` — detalle con variantes visibles, precio en esa moneda y, en modo
  escalas, los tramos con su `finals`.
- `POST …/cart/quote` — precia un carrito (moneda + líneas → totales); lo usa el carrito y el envío.
- `GET …/geography/departments` y `GET …/geography/departments/{id}/cities` — para el formulario de
  facturación y envío; sólo lectura, cacheables.
- `POST …/order-requests` — crea la solicitud con su moneda y sus datos de facturación y envío; valida Turnstile en el backend y vuelve
  a preciar.

`currency` es obligatoria donde aparece y tiene que ser una de las de la tienda; si no, 422. La
caché del servidor de Astro guarda una copia por moneda.

DTOs públicos propios: **nunca** reutilizar los del catálogo interno. No salen costo, pisos, escala
global, ids internos de cuentas ni nada que no se pinte en la tienda.

## 7. Restricciones

- BFF: precios, escalas y totales los calcula el backend.
- `X-Qep-Client` y CSRF no aplican a las rutas públicas (sin cookie); el abuso se controla con
  Turnstile, rate limit y límites de tamaño (líneas por solicitud, longitud de textos).
- Copy en español neutro, tuteando.
- Backend: módulo nuevo con su `<Modulo>LayerTests.cs`, auditoría en el handler, errores mapeados en
  `ApiExceptionHandler.cs`.
- Frontend: feature nueva `features/store/` (configuración y bandeja); `src/routes/` sólo cablea.
- Deploy: backend primero; el backoffice no muestra la sección sin el módulo `store`.

## 8. Lo que queda al builder

- Nombre definitivo del módulo, tablas y app Astro; estructura interna de Astro (islas, SSR con
  caché por tienda).
- Paleta cerrada de colores y mecánica de generación del tema.
- Normalización de teléfonos para el emparejamiento de clientes.
- Paginación, tamaños de página y límites concretos.
- SEO: `sitemap.xml`, `robots.txt` y metadatos Open Graph por tienda.

## 9. Pendiente de verificar antes de construir

- ~~Vercel y dominios comodín~~ Verificado 2026-11-03: no exige los nameservers de Vercel. Basta
  delegar `_acme-challenge` (dos NS a `ns1/ns2.vercel-dns.com`) y `CNAME *` →
  `cname.vercel-dns-0.com`; el resto del DNS se queda donde está
  (vercel.com/docs/domains/working-with-domains/add-a-domain).
- ~~Cómo «Nueva conversación» recibe un teléfono~~ Verificado 2026-11-03: no recibe teléfono.
  `POST …/conversations` lleva `customerId` y el destino lo decide el backend
  (`StartConversationInput`, `features/messaging/types/messaging.ts:672-682`). Lo que se precarga
  es el **cliente del pedido**. Falta ver si el backend reutiliza una conversación existente con
  ese cliente.
- Que el motor de precios de pedidos acepte un pedido nuevo de origen `Store` reutilizando el camino
  de `POST /orders` (manual) sin duplicarlo.
