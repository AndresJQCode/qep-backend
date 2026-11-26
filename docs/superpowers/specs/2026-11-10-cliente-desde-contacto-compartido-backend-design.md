# Escribirle o agregar como cliente a un contacto compartido (backend)

Fecha: 2026-11-10. Repo: `qep-backend`. Contraparte: `qep-frontend`,
`docs/superpowers/specs/2026-11-10-cliente-desde-contacto-compartido-frontend-design.md`. Este spec manda el contrato.

Marcas: **[owner]** decisión del owner; **[orq]** decisión del orquestador, revisable; **[spec]** decisión de
este spec, revisable. Todo nombre marcado **NUEVO** no existe hoy en el código.

## Problema

Un cliente le comparte al negocio, por WhatsApp, la tarjeta de otra persona de su agenda para que le escriban
(un referido). Desde `fix/contactos-compartidos` (`acd6af88`) el asesor ve en la burbuja el nombre, los teléfonos y
los correos de esa tarjeta, pero no puede hacer nada con ellos: para iniciar una conversación,
`POST /messaging/conversations` exige un `customerId` existente (`StartConversation.cs:93-102`), y la consola no
tiene forma de saber si ese número ya es de un cliente.

## Metas

- La tarjeta ofrece, por cada teléfono con WhatsApp, **«Escribirle»** y **«Agregar como contacto»** [owner].
- Si el número **ya es de un cliente**: sólo «Escribirle», que abre directo la lista de plantillas con ese cliente
  [owner]. «Agregar como contacto» no se muestra [owner].
- Si **no es cliente**: las dos opciones abren el modal de crear cliente prellenado y editable; «Escribirle» sigue
  a las plantillas al guardar, «Agregar como contacto» termina ahí [owner].
- El backend le da a la tarjeta lo que la pantalla no puede calcular: el cliente que coincide con cada número y el
  número partido en país y número nacional [orq].

## No metas

- Guardar quién refirió al contacto [owner].
- Agregar el contacto a la agenda de WhatsApp: la Cloud API no tiene una escritura de agenda; en coexistencia
  `smb_app_state_sync` sólo informa los contactos de la app hacia la API [orq, verificado en documentación de BSPs].
- Instagram: no manda tarjetas de contacto.
- La búsqueda de mensajes: `MessageHitDto` (`SearchMessages.cs`) sigue sin `contacts`.
- Un endpoint nuevo para crear clientes incompletos: el alta usa `POST /customers` tal cual [owner, ver abajo].

## Anti-metas

- **No** crear un cliente sin pasar por la política de campos del tenant: el documento y la ciudad los exige o no
  `CustomerFieldPolicy` (`CustomerFieldPolicyRequirement.cs`), igual que en el alta de la consola [owner].
- **No** romper la página de mensajes si la búsqueda del cliente o la partición del número fallan para un teléfono:
  ese teléfono sale sin coincidencia y sin partición, y la burbuja sigue mostrando el número.

## Lo que ya existe y se reutiliza

- `POST /api/v1/tenants/{tenantId}/customers` (`CustomerEndpoints.cs:77`, permiso `customers.customer.manage`): crea un
  cliente `Complete` con CUC; nombre, teléfono y país obligatorios; documento y ciudad según la política del tenant.
- `POST /api/v1/tenants/{tenantId}/messaging/conversations` (`StartConversation.cs`, permiso
  `messaging.conversation.manage`): exige cliente activo con `PhoneE164` o BSUID; si ya hay conversación de ese
  cliente en esa conexión, envía ahí (200) en vez de crear (201).
- `IMessagingCustomerDirectory.MatchAsync` (adaptador `src/Bootstrapper/MessagingCustomerDirectory.cs`, sobre
  `CustomerPhoneDirectory.MatchAsync`): el cliente más antiguo por número, fusionados al final. **No sirve tal cual
  para este spec:** no descarta inactivos (`CustomerPhoneMatch` no lleva `IsActive`) y, si el ganador es un fusionado,
  lo cambia por aquel en el que quedó fusionado con una segunda consulta (`CustomerPhoneDirectory.cs:49-54`).
- El teléfono no es único entre clientes (`IX_customers_tenant_phone_e164` no es único). Por eso la coincidencia que
  devuelve este spec es lo único que evita que el asesor cree un duplicado.

## Contrato

`SharedContactPhoneDto` (`MessagingDtos.cs`) gana tres campos al final, todos nullable:

```
SharedContactPhoneDto(string? Phone, string? WaId, string? Type,
    CustomerRefDto? Customer,        // NUEVO
    string? Country,                 // NUEVO: ISO alfa-2 de la región del número
    string? NationalNumber)          // NUEVO: sólo dígitos, sin prefijo de país
```

```json
{ "phone": "+57 300 123 4567", "waId": "573001234567", "type": "CELL",
  "customer": { "id": "…", "name": "Laura Gómez", "isComplete": false },
  "country": "CO", "nationalNumber": "3001234567" }
```

- Los tres se calculan **sólo desde `waId`** [spec]: un teléfono sin `waId` no tiene WhatsApp, la pantalla no le ofrece
  acciones y los tres viajan `null`.
- `customer` es, entre los clientes **activos y no fusionados** cuyo `phone_e164` es `"+" + waId`, el más antiguo
  por `(created_at, id)`; o `null` si no hay ninguno [spec]. Se **filtra antes** de elegir el más antiguo: si se
  eligiera primero y se descartara después, un cliente viejo inactivo escondería el número para siempre y la tarjeta
  ofrecería «Agregar como contacto» en cada lectura, cada vez con otro duplicado. Un fusionado **no** se cambia por
  aquel en el que quedó fusionado [spec]: ese puede tener otro teléfono, y «Escribirle» le escribiría a un número
  distinto del de la tarjeta (`StartConversation` escribe al `PhoneE164` o BSUID del cliente). Un número que sólo
  tienen inactivos o fusionados sale sin coincidencia; el asesor puede crear uno nuevo, y ese duplicado lo muestra
  la consola de anomalías (`SharedPhone`). `CustomerRefDto` es el que ya viaja en la conversación (`Id`, `Name`,
  `IsComplete`).
- Por eso la regla vive en un método por lote **NUEVO** de `ICustomerPhoneDirectory` (Customers) y de
  `IMessagingCustomerDirectory` (Messaging, adaptador en Bootstrapper), y no en `MatchAsync`, que la bandeja sigue
  usando sin cambios [spec].
- `country` y `nationalNumber` son la partición del E.164 `"+" + waId` con libphonenumber: la región y el número
  nacional en dígitos. Si el número no es válido para ninguna región, los dos viajan `null` y el formulario arranca
  sólo con el nombre. Motivo, para el DTO: el formulario de cliente pide país y número nacional por separado
  (`src/lib/phone.ts` del frontend sólo acepta dígitos); prellenar el `waId` entero con país `CO` deja
  `PhoneE164 = null` y `StartConversation` falla con `422 messaging.customer.unreachable`.
- La coincidencia se calcula **al leer**, no se guarda: si el asesor crea el cliente, la siguiente lectura del hilo
  ya trae `customer`.

## Diseño

- `MessageMapping.ToDto` es estático y síncrono; la coincidencia necesita la base. Se resuelve como los nombres de
  miembros o de chatbots: el handler que arma la página junta los `waId` de los `Contacts` de la página, resuelve
  todo en **una** consulta por lote y le pasa a `ToDto` un diccionario [spec]. Ningún camino hace una consulta por
  teléfono.
- Messaging no ve Customers: la partición del número y la coincidencia entran por `IMessagingCustomerDirectory`
  (un método por lote **NUEVO**, o dos), con el adaptador en Bootstrapper, que sí ve el normalizador de Customers
  [spec]. El nombre del método queda al constructor.
- Todo camino que devuelva un `MessageDto` de tipo `Contacts` pasa por ahí. Hoy los mensajes de un hilo se leen por
  la página de mensajes; el entrante en tiempo real sólo manda ids y el SPA vuelve a pedir la página.
  `MessageMapping.ToDto` también se llama desde `OutboundGuards.cs:223` (envíos salientes) y `SearchMessages.cs:104`
  (fuera de alcance): el constructor confirma que un saliente nunca es `Contacts` y, si lo es, que el diccionario
  vacío deja los campos en `null` sin fallar.

## Pruebas

- Unitarias del mapeo: un `Contacts` con un teléfono que coincide, uno sin coincidencia, uno sin `waId` y uno con
  `waId` no válido.
- Integración: un hilo con una tarjeta cuyo número es de un cliente activo trae `customer`; con sólo un cliente
  inactivo o sólo uno fusionado no; con un inactivo viejo y un activo nuevo trae el activo; después de
  `POST /customers` con ese número, la siguiente lectura lo trae. Y que la página arma la coincidencia
  con una sola consulta, sin importar cuántas tarjetas haya.
- Las pruebas de integración que arman la página de mensajes con objetos anónimos no se rompen: los campos son
  nuevos y nullable.

## Despliegue

Sin migración. El backend puede salir antes que el frontend: un SPA viejo ignora los campos nuevos.
