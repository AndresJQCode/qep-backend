---
paths:
  - "src/Modules/Customers/**"
  - "tests/Modules/Customers/**"
  - "src/Bootstrapper/**/Customer*.cs"
---

# Clientes

Reglas de `qep-backend` que no se deducen leyendo el código. Se cargan solas al leer o editar un
archivo que coincide con `paths` (arriba). Las reglas generales están en [`CLAUDE.md`](../../CLAUDE.md).

- **Un cliente puede ser `Incomplete`** (sin CUC, dirección ni clasificación): lo crean la ingesta
  de Messaging, la importación de pedidos de Shopify (`Customer.CreateIncompleteFromExternalSale`, sin BSUID ni IGSID)
  y `POST /pos/customers` (incompleto **con** documento y teléfono, sin CUC; spec 2026-10-14, POSC-05). Los de
  Messaging y Shopify no traen documento, los del POS sí: el pre-chequeo de identificación de Pos (el 422 con `existing`)
  depende de que `FindExistingIdentificationsAsync` devuelva también a los incompletos dueños de un documento; no lo
  "optimices" con `Completeness == Complete`. Todo código nuevo que lea `Cuc`, `IdentificationType`, `Address`,
  `Country` o `ClassificationId` de un `Customer` tiene que tratar el `null`; Quotations lo rechaza con
  `client_incomplete` **antes** que `client_cuc_missing`, y un reporte que agrupe por clasificación o ciudad
  filtra `Completeness == Complete`.
- **La expresión normalizada del documento vive en dos lugares y tienen que ser iguales carácter por carácter** (spec 2026-10-24).
  `CustomerIdentificationSql.NormalizedNumber` (las consultas de coincidencias y anomalías) y el literal de la migración
  `AddCustomerIdentificationNormalizedIndex` (`upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))`): una migración
  no lee constantes. Si difieren, la consulta sigue dando el resultado correcto pero Postgres deja de usar
  `IX_customers_tenant_identification_normalized` y recorre la tabla, sin error ni prueba de resultado que se ponga roja. Sólo lo
  dice `TheMatchQueryUsesTheNormalizedIndex`; cambia las dos juntas, con una migración nueva para el índice.
- **El `Version++` del fusionado es lo que impide fusionar al mismo cliente en dos que quedan.** `ApplyMergedInto` pasa por
  `Touch` y sube la versión del fusionado, así que dos fusiones simultáneas del mismo cliente hacia distintos que quedan
  chocan por concurrencia optimista, y `merged_into_id` lo saca de toda fusión posterior (`customers.merge.already_merged`).
  No quites el `Touch` ni marques fusionados con un `ExecuteUpdate` que se salte el agregado.
- **La tabla de tipos de documento por país vive una sola vez, en `DocumentTypesByCountry` (`BuildingBlocks.Domain`, carpeta `Identification`).** Companies conserva su tabla tipada
  propia, atada por `DocumentTypesByCountryTests`; Customers (`IdentificationTypeParser`) y Quotations (`PartyFiscalTypesParser`) las atan sus pruebas de paridad. Customers y las
  partes de facturación comparan el tipo con **su propio** país en cada escritura (una fila guardada que no cumple se lee, pero no se puede volver a guardar igual); un país vacío
  en una parte es `CO`.
- **El formato del número de documento vive una sola vez, en `DocumentNumberFormat` (`BuildingBlocks.Domain`, carpeta `Identification`).** `NIT`, `CC` y `CE` solo admiten
  dígitos, punto, guion y espacio (con al menos un dígito); los demás tipos son alfanuméricos y no se tocan. Hr tiene su propia versión, `HrDocumentNumberRules` (CC, CE, PEP y PPT, sin
  espacios), porque sus tipos son otros. Un camino nuevo que escriba un número usa las dos capas: el validador (error de campo `IdentificationNumber`) y el dominio (código
  `<módulo>.…_not_numeric`). La regla bloquea **siempre al escribir**, también un número viejo reenviado sin cambios; al leer no se comprueba (una fila vieja con letras se lee
  igual). No toques `CustomerIdentificationNumbers.Normalize` ni su gemela en SQL: la clave de duplicados sigue igual.
- **La importación por Excel crea etiquetas en tipo oración y nunca las quita** (spec 2026-11-17). La celda «Etiquetas» se lee
  una sola vez con `ExcelTagCell.Parse` (validador y handler), la etiqueta nueva se bautiza con `TagNameFormat.SentenceCase` y la
  que ya existe conserva su nombre aunque el archivo la traiga con otras mayúsculas. Un camino nuevo que escriba etiquetas desde
  un archivo pasa por esas dos funciones; y no «simplifiques» la celda a «reemplaza la lista»: la exportación escribe las
  actuales, pero un archivo armado a mano borraría las que puso el asesor. Y `Direccion` dejó de ser obligatoria en el Excel
  (`ca9ee15c`): es opcional en el formulario y en el dominio, y la regla vieja del importador sólo impedía cargar listas de
  nombre y celular.
- **`PosCustomerLookupTests.TheIdentificationTypesMatchTheCustomersModule` compara Pos con `AllowedFor("CO")`, igual que la importación de clientes por Excel y la de contratos de
  servicio.** Ensanchar Customers no ensancha a ninguno de ellos; si el owner lo quiere, es una decisión aparte.
- **`MergeForStore` toma el país de la solicitud junto con su documento cuando el cliente no tiene documento** (la dirección sale de la solicitud y el teléfono conserva su E.164
  anterior cuando se reemplaza el país); `CustomerMergePlan` rellena documento y país como un par.
- **`CustomerReferenceLock` serializa el borrado contra quien referencia al cliente** (advisory lock por cliente, `Exclusive` para el
  borrado, `Shared` para escritores). Lo toman las unidades de trabajo de Quotations, Loans, ServiceContracts y Support; en Messaging, el ingreso
  (entrante y coexistencia), el opener, el cambio de cliente y el despacho de campañas, con **un solo candado compartido antes de
  cualquier candado de fila de conversación**. El borrado además toma el candado por conversación del runner de Automations
  (`AutomationsConversationLock.Namespace`) antes del `FOR UPDATE` de `conversations`: sin ese orden hay un ciclo de espera que la base
  no detecta. Todo esto exige `READ COMMITTED`; en `REPEATABLE READ` el chequeo de bloqueadores vería una foto vieja. El POS y los
  escritores del pipeline **no** lo toman: una venta creada en plena corrida puede conservar el id borrado (su foto sigue mostrándose).
