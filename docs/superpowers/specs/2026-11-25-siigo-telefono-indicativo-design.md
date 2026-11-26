# Siigo: teléfono del cliente con indicativo — diseño (backend, entrega 1)

Fecha: 2026-11-25 · Repo: `qep-backend` · Rama: `feature/telefono-pais` · Estado: **aprobado** (owner, 2026-11-25). Está
implementado y mergeado en `develop` el 2026-11-25 (merge `da98b8e20`), pendiente de despliegue; las pruebas quedaron escritas,
pero no se han corrido.
Plan: `docs/superpowers/plans/2026-11-25-siigo-telefono-indicativo.md`. Hechos reverificados contra `develop` en
`8a9bfd50d` (después de los cambios de Siigo del 2026-11-24 y 25, incluida la etapa contract): archivos, firmas y criterios siguen
como se describen. `IssueSiigoInvoice.EnsureCustomerAsync` es privado y hoy lo corre sólo el worker de las tandas.
Spec hermano: `qep-frontend/docs/superpowers/specs/2026-11-25-telefono-selector-pais-design.md` (entregas 2 y 3).

## Intención

Crear el cliente en Siigo falla cuando el teléfono trae el indicativo. QEP guarda casi siempre E.164 (`+573001234567`), y hoy ese
texto viaja tal cual en `phones[0].number`:

- `ExternalOrderInvoicingService.ResolvePartyAsync` toma el teléfono de la parte facturada y, si no hay, el de la ficha del cliente
  (`FirstNonBlank(billing?.Phone, customer?.Phone)`; el de la ficha es `Customer.Phone`, como se escribió, vía
  `QuotationCustomerLookup.ToRef`).
- `SiigoOrderSource` (Bootstrapper) lo pasa sin tocar a `SiigoBilledPartyInput.Phone`.
- `SiigoCustomerMapper.Map` sólo recorta (`Clean`), y `SiigoJson.CustomerBody` manda `phones = [{ number }]`, sin `indicative`.

Según la documentación de Siigo ([crear cliente](https://developers.siigo.com/docs/siigoapi/customer/1-create-customer/)),
`phones[].indicative`, `number` y `extension` son textos opcionales, sólo dígitos, sin espacios ni caracteres especiales, de máximo
10 cada uno. `+573001234567` incumple las tres reglas en `number`. **No se reprodujo contra Siigo real.**

Decisión del owner (2026-11-25): QEP guarda E.164 como formato neutro y **cada integración lo convierte a las reglas de su
proveedor**. El partido del número tiene que servir también a futuras integraciones contables (España). No se crea ahora una
abstracción de «proveedor contable».

## Objetivos

- El cliente que se crea en Siigo lleva `indicative` (código de país, p. ej. `57`) y `number` (número nacional, p. ej. `3001234567`).
- Un teléfono que no se puede partir no tumba la creación: se mandan sus dígitos si son 10 o menos y, si no, se omite `phones`.
  El cliente se crea igual.
- El partido vive en el puerto compartido `IPhoneNumberSplitter` (Customers.Application), reusable por otra integración.

## No objetivos

- No se cambia lo que QEP guarda ni se migran datos.
- No se toca un cliente que ya existe en Siigo: `IssueSiigoInvoice.EnsureCustomerAsync` lo busca por identificación y, si existe,
  la factura va sólo con ella.
- No se manda `extension`.
- No se crea una abstracción de proveedor contable.
- El formato del teléfono en el Excel de pedidos, el PDF y la tienda no cambia aquí (ver Hallazgos y Decisiones del owner).

## Hallazgos

1. **Cómo trata libphonenumber los valores viejos** (verificado con `libphonenumber-csharp` 9.0.41, la versión de
   `Directory.Packages.props`, con un script `dotnet fsi`, no con una prueba del repo):

   | Entrada | Región `ZZ` (lo que hace `Split` hoy) | Región `CO` |
   | --- | --- | --- |
   | `+573001234567`, `+57 300 123 4567`, `+57-300-123-4567` | 57 / `3001234567` | igual |
   | `573001234567` (sin `+`) | excepción `INVALID_COUNTRY_CODE` → `null` | 57 / `3001234567` (quita el 57 porque 12 dígitos es largo para CO) |
   | `3001234567`, `300 123 4567` | `null` | 57 / `3001234567` |
   | `6012345678` (fijo) | `null` | 57 / `6012345678` |
   | `584121234567` (VE sin `+`) | `null` | inválido → `null` |
   | `+57573001234567` (prefijo doble) | inválido → `null` | inválido → `null` |
   | `+34612345678` | 34 / `612345678` | igual |

   Con `+` la región se ignora; sin `+`, el número se lee como nacional de la región dada. Hoy `Split` usa siempre `ZZ`, así que
   **todo valor sin `+` sale `null`**. Para que `573001234567` termine en `57` + `3001234567`, `Split` necesita una región por defecto.

2. **De dónde salen los valores sin `+`.** El frontend quita el `+` con `sanitizePhoneInput` en la libreta de direcciones y en las
   partes de cotización/pedido, y `cleanSuggestedPhone` lo quita al prellenar la empresa desde Shopify: un `+57 300…` editado queda
   `57300…`. La tienda (`StoreCheckoutParties`, `PhoneE164`) y Shopify ya escriben partes con `+`.

3. **Consumidor final:** `ResolvePartyAsync` devuelve teléfono `null`, así que nunca viaja `phones`. El mapper conserva su rama.

4. **Teléfonos con espacios o guiones** (partes importadas de Shopify): libphonenumber los parte bien (tabla). Lo que no se pueda
   partir se limpia a dígitos antes de aplicar el tope de 10.

5. **`IPhoneNumberSplitter` ya se usa desde Bootstrapper** (`MessagingCustomerDirectory`, siempre con E.164 `"+" + waId`). Su única
   implementación es `LibPhoneNumberNormalizer`, registrada como singleton en `CustomersInfrastructureExtensions`. `PhoneNumberParts`
   sólo se construye ahí y en `LibPhoneNumberNormalizerTests`.

6. **Lector relacionado que se rompe con E.164 cortos: `WhatsAppPhone.Normalize` (Quotations).** Le antepone `57` a todo número
   de 10 dígitos, aunque traiga `+`. Un E.164 de 10 dígitos en total (Belice `+501` + 7, Guyana `+592` + 7, Surinam `+597` + 7,
   Panamá fijo `+507` + 7) sale como `57…` y el WhatsApp de cotización, pedido o factura va a otro número. Ya pasa con la ficha del
   cliente; las entregas 2 y 3 lo extienden a partes y direcciones. Lo usan `WhatsAppSenderRouter`, `ZenviaWhatsAppSender` e
   `InvoiceWhatsAppDelivery`.

7. **Ningún validador del backend rechaza el `+`** en los campos que el frontend cambia (todos sólo topan el largo, y E.164 tiene
   máximo 16 caracteres): `QuotationPartyDetails.PhoneMaxLength` 32, `CustomerAddress.PhoneMaxLength` 32,
   `CompanyContactInfo.PhoneMaxLength` 32, `SupplierContactInfo`/`SupplierContact` 32, `PosCustomerRules.PhoneMaxLength` 32,
   `AppointmentRequestValidators.PhoneMaxLength` 32, `UpdateStoreSettings.WhatsAppMaxLength` 32, `ShopifyPartyLimits.PhoneMaxLength`
   32. El distribuidor ya normaliza a E.164 (`DistributorWhatsAppNotice`, tope 16) y el miembro ya exige E.164
   (`MemberPhone.IsValid`). **El frontend no necesita cambios de backend para guardar E.164.**

8. **Quién lee los teléfonos que pasan de sólo dígitos a E.164** (detalle por formulario en el spec del frontend):

   | Lector | Efecto | Manejo |
   | --- | --- | --- |
   | Siigo (`SiigoOrderSource`) | se rompe hoy con `+` | este spec |
   | WhatsApp de cotización/pedido/factura (`WhatsAppPhone`) | bien para CO (12 dígitos); mal para E.164 de 10 dígitos | este spec, cambio 2 |
   | Shopify (`ShopifyOrderUpdateClient`, `ShopifyOrderProjection.SamePhone`) | Shopify acepta E.164; la comparación ya ignora el indicativo (review I-3) | nada |
   | Excel de pedidos (`OrdersExportProcessor`, columnas `phone`, `billing_*`, `shipping_*`) | sale `+573001234567` donde salía `3001234567`; `customer_*` y el respaldo de `phone` ya salían en E.164 cuando la ficha lo tiene | no cambia (decisión del owner 1) |
   | PDF de cotización/pedido (`QuotationPdfDocumentMapper`), ticket del POS, membrete de soporte | se ve `+573001234567` sin agrupar | cosmético; nada ahora (mi decisión) |
   | Tienda (`PublicStoreContactDto.WhatsAppUrlOf`) | `wa.me` exige indicativo: con E.164 el enlace **queda bien**; hoy un `3001234567` arma un enlace sin país | nada; mejora |
   | Coincidencias (clientes, proveedores, recordatorios de agenda) | usan `PhoneE164`, que se calcula igual con o sin `+` | nada |
   | Correos | no encontré un correo que lea estos teléfonos | nada |

## Enfoques considerados

1. **Regla sólo de Siigo («si empieza por 57, cortarlo»).** Cuatro líneas en el mapper. Se descarta: no sirve para otros países
   (España, Venezuela), confunde un nacional que empiece por 57 y no se puede reusar en otra integración.
2. **Partir con libphonenumber en el puerto compartido `IPhoneNumberSplitter` (elegido).** El puerto ya existe, ya lo consume
   Bootstrapper, y la librería conoce los indicativos y los largos de cada país. Siigo sigue sin referenciar Customers: el partido
   ocurre en `SiigoOrderSource`, que ya traduce datos de Quotations para Siigo.

## Diseño

### Cambio 1: el puerto devuelve el indicativo y acepta una región por defecto

- `PhoneNumberParts(string Country, string NationalNumber, string CallingCode)`: `CallingCode` son los dígitos del código de país
  (`"57"`, `"34"`), de `parsed.CountryCode`.
- `IPhoneNumberSplitter.Split(string? phone, string? defaultRegion = null)`. Con `+` la región se ignora. Sin `+`, el número se lee
  como nacional de `defaultRegion` (ISO alfa-2); sin región (o vacía) se usa `ZZ`, que es el comportamiento de hoy. Sigue
  devolviendo `null` si no parsea, no es válido o no tiene región, y nunca lanza.
- `MessagingCustomerDirectory` no cambia (manda E.164 y no pasa región).

### Cambio 2 (mi decisión): `WhatsAppPhone.Normalize` respeta el `+`

Con un `+` adelante devuelve sus dígitos tal cual, sin anteponer `57`. Sin `+`, la regla de hoy (10 dígitos → `57` + dígitos).
Es Quotations, no Siigo, pero las entregas 2 y 3 extienden el daño y el arreglo es de una línea. Si el owner lo prefiere aparte,
sale de este spec sin tocar el resto.

### Cambio 3: Siigo recibe indicativo y número

- **Siigo.Application:** un record nuevo `SiigoPhone(string? Indicative, string Number)` con `ToString` que no escribe nada (lleva
  datos del cliente, como `SiigoBilledPartyInput`). `SiigoBilledPartyInput.Phone` y `SiigoCustomerRequest.Phone` pasan de `string?`
  a `SiigoPhone?`.
- **`SiigoOrderSource`** recibe `IPhoneNumberSplitter` por constructor y arma el teléfono:
  - vacío → `null`;
  - `Split(phone, región)`, con región `CO` si `party.Country` está vacío o es `CO` (el mismo criterio de `IsColombian`) y, si no,
    `party.Country`. Partido → `new SiigoPhone(parts.CallingCode, parts.NationalNumber)`;
  - no partido → `new SiigoPhone(null, phone)`: el mapper decide si viaja.
- **`SiigoCustomerMapper`** aplica las reglas de Siigo en las dos ramas (persona/empresa y consumidor final):
  - `Number` → sólo dígitos ASCII. Vacío o más de 10 → **sin teléfono** (`Phone = null`).
  - `Indicative` → sólo dígitos. Vacío o más de 10 → `null` (viaja el número solo).
- **`SiigoJson.CustomerBody`** manda `phones = [{ indicative, number }]`; sin indicativo, el objeto no lleva la propiedad
  `indicative` (no `null`). Sin teléfono no hay `phones`, como hoy.

Resultado por caso:

| Teléfono en QEP | País de la parte | `phones` |
| --- | --- | --- |
| `+573001234567` / `+57 300 123 4567` | cualquiera | `[{ indicative: "57", number: "3001234567" }]` |
| `573001234567` / `3001234567` | vacío o `CO` | `[{ indicative: "57", number: "3001234567" }]` |
| `+34612345678` | cualquiera | `[{ indicative: "34", number: "612345678" }]` |
| `4121234567` | `VE` | `[{ indicative: "58", number: "4121234567" }]` |
| `12345` (no válido) | `CO` | `[{ number: "12345" }]` |
| `+57573001234567` / `584121234567` con `CO` | — | sin `phones` |
| consumidor final | — | sin `phones` |

`request_json` de la factura no cambia (el cliente va en una llamada aparte). `siigo.call_failures` guardará el curl con el cuerpo
nuevo, igual que hoy.

### Despliegue

El backend va **antes** que la entrega 2 del frontend: esa entrega hace que las partes facturadas guarden E.164, que es justo lo que
hoy rompe la creación en Siigo. Sin migraciones ni configuración nueva. `docs/estado.md` se actualiza en `develop` al mergear, no en
esta rama.

## Pruebas (RED primero)

**Instrucción del owner (2026-11-25): durante la implementación nadie corre pruebas**, ni unitarias ni de integración. Las pruebas
se escriben primero (nombres y aserciones), pero toda verificación es `dotnet build` de los proyectos afectados, incluidos los de
pruebas. El RED es de compilación donde cambia una firma (`PhoneNumberParts`, `SiigoPhone`, el constructor de `SiigoOrderSource`):
la evidencia es la salida del build. Donde la prueba compila contra el código de hoy (`WhatsAppPhone`), el RED no se puede mostrar
sin correrla y se deja dicho. Todo es unitario; no hace falta integración nueva. Las pruebas las corre el owner
(`.\scripts\test-feature.ps1` antes de mergear a `develop`).

1. `LibPhoneNumberNormalizerTests` (Customers.UnitTests):
   - `Split` devuelve `CallingCode`: `+573001234567` → (CO, `3001234567`, `57`), `+34612345678` → (ES, `612345678`, `34`),
     `+390612345678` → (IT, `0612345678`, `39`).
   - Con región: `573001234567`, `3001234567` y `300 123 4567` con `CO` → (CO, `3001234567`, `57`); `+57 300 123 4567` sin región
     → igual.
   - Sin `+` y sin región sigue `null` (comportamiento de hoy); `584121234567` con `CO` y `+57573001234567` → `null`.
   - Ajustar la prueba existente que construye `PhoneNumberParts` con dos argumentos.
2. `WhatsAppPhoneTests` (Quotations.UnitTests): `+5016123456` → `5016123456`; `+573001234567` → `573001234567`; `3001234567` →
   `573001234567` (sin cambio).
3. `SiigoCustomerMapperTests`: indicativo y número limpios a dígitos; número de más de 10 dígitos → sin teléfono; indicativo
   inválido → sólo número; consumidor final sin teléfono.
4. `SiigoApiClientTests` (`CustomerBody`): `phones[0].indicative == "57"` y `number == "3001234567"`; sin indicativo no existe la
   propiedad; sin teléfono no existe `phones`. Se actualiza la aserción actual que espera `number` solo.
5. `SiigoOrderSourceTests` (Bootstrapper.UnitTests), con un splitter de prueba que registra la región recibida: E.164 →
   `SiigoPhone("57", "3001234567")`; país vacío y `CO` → región `CO`; `VE` → región `VE`; no partido → `SiigoPhone(null, crudo)`;
   sin teléfono → `null`.

## Riesgos

- **No probado contra Siigo real.** No hay empresa de pruebas (regla `siigo`): la primera factura real con cliente nuevo es la
  prueba. Se revisa en `siigo.call_failures` si falla.
- La documentación no dice si Siigo valida el largo del número según el indicativo; un número de 7 dígitos (no partido) podría
  rechazarse. El cliente igual se crearía si Siigo trata `phones` como opcional, cosa que no está verificada.

## Decisiones del owner (2026-11-25)

1. **El Excel de pedidos que importa el ERP no cambia en este trabajo.** Con las entregas 2 y 3, `phone`, `billing_*` y
   `shipping_*` pasan a salir `+573001234567` (`customer_*` y el respaldo de `phone` ya salían en E.164 cuando la ficha lo tiene).
   **Seguimiento del owner:** confirmar con el tenant que importa el Excel que su ERP acepta `+57…`. Si necesita 10 dígitos, se
   agrega un paso de formato en la exportación (spec aparte), reusando `IPhoneNumberSplitter`.
2. **El WhatsApp de la tienda (`store-settings-form`) entra en la entrega 3 del frontend** con el selector de país. Antes de
   desplegar esa entrega se revisa en `qep-storefront` cómo pinta `contact.whatsApp` (ver spec del frontend).
3. **Cambio 2 (`WhatsAppPhone` respeta el `+`) queda en este spec** (aprobado con el resto).

## Adición: `id_type` y formato del documento (aprobada por el owner, 2026-11-25)

**Evidencia de producción:** 11 llamadas `CustomerCreate` con 400 por `id_type` y 1 `CustomerFind` con 400 por una identificación con
puntos. El problema del teléfono de este spec se dedujo del código y de la documentación de Siigo, no de estos registros.

**`id_type`:** en `POST /v1/customers` viaja como texto (`"13"`), no como `{ "code": "13" }`.

**Documento del comprador** (`SiigoCustomerMapper.Identification`; es el que usas para buscar el cliente, crearlo y facturar). Sólo
cambia lo que se manda: lo guardado en QEP queda igual.

| Tipo | Lo que viaja |
| --- | --- |
| Consumidor final | el documento genérico, como antes |
| CC | sólo dígitos ASCII |
| NIT que termina en guion y un solo dígito (con o sin espacios) | los dígitos anteriores al guion |
| NIT sin ese final | sólo dígitos; si quedan 10, empiezan por 8 o 9 y el último es el dígito de verificación de los 9 primeros, se quita |
| CE y pasaporte | sólo letras y dígitos ASCII, con las mayúsculas como se escribieron |
| Otro tipo o sin tipo | sin espacios en los bordes, como antes |

Un guion en otra parte del NIT ya no corta el número. Si no queda nada, el documento cuenta como faltante, igual que antes.

Dígito de verificación de la DIAN: pesos 3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67 y 71 desde la cifra de la derecha;
`s` = suma módulo 11; el dígito es `11 - s` si `s > 1` y, si no, `s`.

**Riesgo:** una factura `Pending` reenvía los bytes guardados, con la forma anterior. Si Siigo ya tiene un cliente con una forma
distinta de la nueva (por ejemplo, uno hecho a mano con el dígito de verificación pegado), la búsqueda no lo encuentra y la siguiente
factura armada intenta crear otro. No hay código para ese caso.

Aceptado por el owner (2026-11-25): no se agrega una segunda búsqueda; si aparece un duplicado, se revisa en Siigo.
