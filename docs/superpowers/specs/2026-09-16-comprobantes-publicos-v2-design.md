# Comprobantes de pago v2: temporal, procesar y mover al bucket público — diseño

**Fecha:** 2026-09-16 (diseño acordado con el owner el 2026-09-15 y el 2026-09-16)
**Rama:** `feature/comprobantes-publicos-v2` (backend), más un cambio de una línea en `qep-frontend`.
**Spec anterior:** [2026-09-15-comprobantes-publicos-design.md](2026-09-15-comprobantes-publicos-design.md). Esta versión cambia **dónde vive** el comprobante; el Excel, la columna `public_storage_key` y el puerto `IPaymentProofPublisher` de v1 se conservan.

## Contexto

v1 está desplegado desde el 2026-09-15: al adjuntar un comprobante a un pedido, se **copia** del bucket privado (`qep-private`) al público (`qep`) con una clave aleatoria, y el original sigue en el privado. El owner no quiere el comprobante duplicado: quiere que se suba a una ruta temporal, que al terminar bien el proceso las imágenes bajen de calidad y pasen a WebP, y que el archivo se **mueva** al bucket público.

Lo que ya existe y este diseño reusa:

- **La ruta temporal.** Toda subida va con URL firmada a `staging/tenants/{tenant}/{id}`; `CompleteUpload` valida tamaño, tipo y antivirus y recién ahí promueve a `files/` y borra el temporal (`CompleteUpload.cs:31-131`).
- **El procesado de imágenes.** `ImageSharpVariantGenerator` (SixLabors.ImageSharp 3.1.12) ya genera WebP con tope de píxeles de entrada y salida.
- **El barrido de abandonados.** `StagingCleanupWorker` borra lo que quedó en `PendingUpload` con más de `StagingRetentionHours` (24 h), cada `StagingCleanupMinutes` (60 min).
- **Las sondas de referencia entre módulos.** `IUserReferenceProbe` (BuildingBlocks) y `OrphanUserCleanupWorker`: cada módulo dice si todavía referencia algo, sin que el worker conozca a los demás.
- **El outbox transaccional** con inbox idempotente por consumidor, que ya usan `SessionRevocationWorker` y `OrphanUserCleanupWorker`.

Datos verificados que condicionan el diseño:

- `FileOwnerType` tiene `User = 1`, `Entity = 2`, `System = 3`, `Product = 4` (`FileResourceEnums.cs:19-28`); `Product` se agregó en CAT-05 para distinguir archivos de catálogo.
- El endpoint de subida rechaza un `ownerType` desconocido con `storage.file.owner_type_invalid` (`StorageEndpoints.cs:84-98`).
- En `qep-frontend`, `uploadQuoteFile` sube con `ownerType: 'User'` y sólo lo usan los comprobantes: `use-convert-quote-to-order.ts` y `use-add-order-payment-proofs.ts`.
- `OrderPaymentProofResolver` acepta `application/pdf`, `image/jpeg` e `image/png`; `FileUploadPolicy` ya acepta `.webp`.
- `IssueDownloadUrlHandler` firma con `Content-Disposition: attachment` sobre `resource.StorageKey` (`IssueDownloadUrl.cs:43-52`).

## Decisiones

| # | Decisión | Por qué |
| --- | --- | --- |
| D1 | Los PDF se mueven tal cual; sólo las imágenes se recomprimen y pasan a WebP. | Un PDF no se convierte a WebP ni se le baja la calidad. |
| D2 | Aplica sólo a comprobantes de pago, con `FileOwnerType.PaymentProof = 5`. | Storage es genérico; el resto de los archivos sigue como hoy. Mismo patrón que `Product` en CAT-05. |
| D3 | El comprobante se mueve al público **al adjuntarlo** al pedido. | Nada llega al público sin un pedido que lo justifique. |
| D4 | Entre la subida y el adjunto, el archivo espera en `staging/` y **nunca se promueve** a `files/`. | Es la ruta temporal que pidió el owner. |
| D5 | El endpoint de descarga de Storage devuelve la URL pública cuando el comprobante ya se movió. | Storage es dueño de dónde está el archivo; el frontend y el contrato de la API no cambian. |
| D6 | La limpieza corre como workers dentro de la API, no como API más `CronJob`. | Sigue el patrón del repo, no necesita credencial de máquina ni abre un endpoint destructivo sin tenant. |
| D7 | Imágenes: lado mayor ≤ 2000 px, WebP calidad 80, sin agrandar. | El texto de un comprobante sigue legible y el archivo pesa una fracción del original. |
| D8 | La imagen se procesa **al completar la subida**; al adjuntar sólo se mueve. | El error de una imagen corrupta aparece al subirla, la conversión sigue rápida y el rollback no deshace un procesamiento. |
| D9 | Al adjuntar se **copia** antes de guardar el pedido; el temporal se borra **después**, por un evento de outbox que consume Storage. | No hay transacción entre las bases de Quotations y Storage; así el comprobante nunca deja de existir en algún lado. |
| D10 | `FileResource` gana un método propio para registrar el movimiento de un comprobante; `Publish` no se toca. | `Publish` sólo acepta imágenes y protege el endpoint de publicación de Storage. |
| D11 | `StagingCleanupWorker` también borra comprobantes `Available`, sin mover, con más de 24 h y **sin referencia** de ningún módulo. | Con D4 quedan `Available` en `staging/`, y el barrido actual sólo mira `PendingUpload`. |
| D12 | Un worker de reconciliación recorre sólo `payment-proofs/` del bucket público, salta lo de menos de 24 h, corre una vez al día y arranca en modo solo-registrar. | Borra objetos cuya URL puede estar en un Excel ya enviado: primero se verifica en los logs. |
| D13 | Los comprobantes `User` (los de v1 y los de la transición) no cambian. Sin migración ni backfill. | Siguen funcionando con el original privado y su copia pública. |
| D14 | Se despliega primero el backend y después el frontend. | El backend nuevo funciona igual con el frontend viejo; al revés, el endpoint rechazaría `PaymentProof`. |

Los nombres de tipos, métodos, eventos y claves de configuración de abajo son **nombres propuestos**; el plan los fija.

## Sección 1: subida y procesamiento

**Cómo se reconoce un comprobante.** Se agrega `FileOwnerType.PaymentProof = 5`. En `qep-frontend`, `uploadQuoteFile` manda `ownerType: 'PaymentProof'`.

**Qué hace `CompleteUpload` con un `PaymentProof`.** Las validaciones de hoy no cambian: tamaño, que el contenido coincida con el tipo declarado y antivirus. Si el veredicto es limpio:

| Tipo | Qué pasa |
| --- | --- |
| `image/jpeg`, `image/png`, `image/webp` | Se redimensiona a lado mayor ≤ 2000 px, sin agrandar, y se codifica WebP calidad 80 con ImageSharp, en un puerto nuevo de Storage (`IPaymentProofImageProcessor`). El resultado **reemplaza** al objeto en `staging/`, y el `FileResource` actualiza tipo (`image/webp`), extensión del nombre (`.webp`), tamaño y checksum. |
| `application/pdf` | Queda tal cual. |

En los dos casos: sin promoción a `files/`, sin miniatura, y el recurso pasa a `Available` con `MarkClean`, que ya existe y no cambia la clave.

**Si la imagen no se puede procesar**, cuarentena y `storage.file.rejected` / `image_processing_failed`, el mismo camino que usa hoy la miniatura (`CompleteUpload.cs:79-91`).

**En Quotations**, `OrderPaymentProofResolver` acepta además `image/webp`.

## Sección 2: el movimiento al adjuntar

En `ConvertQuotationToOrderHandler` y `AddOrderPaymentProofsHandler`, sin cambiar su forma actual:

1. **Antes del dominio, copia al público.** `PaymentProofCopies` y `PublicPaymentProofPublisher` ya copian desde `resource.StorageKey`; para un `PaymentProof` esa clave es la de `staging/`. Sólo se suma `image/webp → .webp` a la tabla de extensiones. El temporal **no** se borra acá.
2. **Se guarda el pedido** con la clave en `OrderPaymentProof.PublicStorageKey`, como hoy, y en la **misma transacción** se escribe en el outbox `quotations.order.payment-proofs-attached.v1` con `tenantId`, `orderId` y, por cada comprobante nuevo, `fileId` y `publicStorageKey`.
3. **Si algo falla antes de guardar**, el rollback de v1 borra las copias públicas; el original sigue en `staging/`.
4. **Un worker de Storage** (`PaymentProofMoveWorker`) consume el evento con su inbox. Para cada `fileId` de un `FileResource` `PaymentProof` todavía sin mover, en este orden:
   1. **borra el objeto de `staging/`**, que ya tiene su copia pública. Borrar una clave que ya no existe no falla, así que el paso es repetible;
   2. **en un solo `SaveChanges`**, llama a `FileResource.MoveToPublic(publicStorageKey, occurredAt)` y marca el inbox.

   Si el borrado falla, no se guarda nada y el mensaje vuelve en el tick siguiente. Si falla el guardado, el tick siguiente repite el borrado (sin efecto) y guarda. El orden inverso —guardar y después borrar— dejaría un objeto huérfano en `staging/` para siempre si el borrado fallara: el inbox ya estaría marcado, y el barrido de la sección 3 sólo mira comprobantes **sin** mover.

`MoveToPublic` sólo es válido para `OwnerType == PaymentProof` y `Status == Available`, acepta cualquier tipo del comprobante (PDF incluido), registra `PublicStorageKey` y `PublishedAt`, y es idempotente con la misma clave. Los comprobantes `User` se ignoran.

**La ventana.** Entre que se guarda el pedido y que el worker procesa (segundos), el archivo existe en los dos buckets y la descarga desde la app usa el temporal. Entre el borrado del temporal y el guardado del paso 4 (milisegundos), una descarga desde la app fallaría y funcionaría al reintentar; el Excel no se ve afectado, porque enlaza la copia pública.

## Sección 3: descarga desde la app y comprobantes que nadie adjuntó

**Descarga.** En `IssueDownloadUrlHandler`, si el recurso es `PaymentProof`, tiene `PublicStorageKey` y no se pidió variante, devuelve `IPublicObjectStorage.GetUrl(PublicStorageKey)`. Todo lo demás firma como hoy. La auditoría `storage.file.downloaded` se registra igual.

- **Cambio de comportamiento aceptado:** un comprobante ya movido se **abre** en una pestaña en vez de descargarse con su nombre original, porque un objeto público no lleva `Content-Disposition` por request. Es lo mismo que hace el enlace «Ver» del Excel.

**Comprobantes que nadie adjuntó.** `StagingCleanupWorker`, con el mismo reloj y `StagingRetentionHours`, suma una segunda búsqueda: `PaymentProof`, `Available`, sin `PublicStorageKey` y con `CreatedAt` anterior al corte. Para cada uno:

- consulta `IFileReferenceProbe` (BuildingBlocks, mismo diseño que `IUserReferenceProbe`; Quotations la implementa: ¿algún `OrderPaymentProof` tiene ese `FileId`?);
- si está referenciado, lo deja: es un comprobante adjunto cuyo evento de la sección 2 todavía no se procesó;
- si no, borra el objeto de `staging/`, marca el recurso como eliminado y audita `storage.file.purged` con motivo `payment_proof_not_attached`.

**Carrera aceptada.** Adjuntar un comprobante subido hace más de 24 h mientras el barrido lo borra hace fallar la copia: el request devuelve error, no se guarda nada y la asesora lo vuelve a subir. Sin pérdida de datos.

## Sección 4: reconciliación, convivencia y despliegue

**Reconciliación de objetos huérfanos.** `PaymentProofOrphanCleanupWorker`, en Storage:

- recorre **sólo** el prefijo `payment-proofs/` del bucket público, paginando; nunca `tenants/.../media`, `quotations/` ni el bucket privado. `IPublicObjectStorage` gana un listado por prefijo con la fecha de modificación de cada objeto;
- salta los objetos con menos de `Storage:PaymentProofOrphanCleanup:MinimumAgeHours` (24), porque al adjuntar se copia antes de guardar el pedido;
- un objeto está referenciado si lo tiene algún `FileResource.PublicStorageKey` (Storage) o algún `OrderPaymentProof.PublicStorageKey` (Quotations, por `IPublicObjectReferenceProbe`), lo que cubre v1 y v2;
- si nadie lo referencia: con `Storage:PaymentProofOrphanCleanup:DryRun = true` sólo registra en el log la clave que borraría; con `false` la borra y audita `storage.public_object.purged`;
- corre cada `Storage:PaymentProofOrphanCleanup:IntervalHours` (24).

`DryRun` va en `true` en `appsettings.json` y en `k8s/prod-configMap.yaml`. Se apaga a mano después de revisar en los logs de producción que lo que marcaría como huérfano realmente lo es.

**Convivencia con v1.** Los comprobantes `User` conservan su original privado, su copia pública y su clave en `OrderPaymentProof`: el Excel los sigue enlazando, la app los sigue descargando del privado, el barrido de la sección 3 no los mira y la reconciliación los encuentra referenciados.

**Orden de despliegue.** Primero el backend (acepta `PaymentProof`, mueve, limpia en solo-registrar); después el frontend (manda `PaymentProof`). Mientras quede abierta una pestaña con el frontend viejo, sus comprobantes siguen el camino de v1.

## Riesgos y contras aceptados

- **Se descarta el original de las imágenes.** Queda el WebP de ≤ 2000 px y calidad 80 como única evidencia del pago (D7).
- **Nada se revoca.** Un comprobante movido es público para quien tenga la URL, igual que en v1.
- **El nombre original se pierde** al abrir un comprobante movido desde la app (sección 3).
- **`DryRun` se apaga a mano.** Mientras esté en `true`, los huérfanos se acumulan en el bucket público.

## Pruebas (TDD: RED antes que GREEN, con evidencia literal)

**Unitarias:**

- el procesador reduce a lado mayor ≤ 2000 px, no agranda, codifica WebP calidad 80 y rechaza una imagen corrupta;
- `FileResource.MoveToPublic` sólo acepta `PaymentProof` `Available`, acepta PDF y es idempotente;
- `OrderPaymentProofResolver` acepta `image/webp`;
- la tabla de extensiones del publicador suma `.webp`;
- `IssueDownloadUrlHandler` devuelve la URL pública sólo para un `PaymentProof` movido.

**De integración:**

- un `PaymentProof` de imagen queda WebP, `Available`, en `staging/` y sin variantes; un PDF queda intacto;
- convertir y sumar comprobantes copian al público y escriben el evento en el outbox; el worker completa el movimiento y borra el temporal;
- una falla antes de guardar borra la copia pública y deja el temporal;
- el barrido borra el comprobante no adjuntado con más de 24 h y respeta el adjuntado;
- la reconciliación, con `DryRun = false`, borra el huérfano viejo, respeta el referenciado y el reciente, y no lista otros prefijos; con `DryRun = true` no borra nada;
- los comprobantes `User` siguen el camino de v1;
- toda factoría de integración fija `Storage:PaymentProofOrphanCleanup:DryRun` explícitamente.

**Frontend (`qep-frontend`):** `uploadQuoteFile` manda `ownerType: 'PaymentProof'` (Vitest).

## Enmiendas (2026-09-16, después de escribir el plan)

Al escribir el plan aparecieron conflictos entre el código y este spec (D15–D18), y al rebasar la rama sobre `develop` apareció uno más (D19). Se resuelven así:

| # | Decisión | Por qué |
| --- | --- | --- |
| D15 | `SoftDeleteFileHandler` y `UnpublishFileHandler` rechazan un `PaymentProof` que algún pedido referencia (`IFileReferenceProbe`) con `storage.file.invalid_state`. `PublishFileHandler` rechaza siempre un `PaymentProof`, con el mismo código: un comprobante sólo llega al público por el movimiento de la sección 2. | Decidido por el owner. Hoy los dos primeros borran lo que haya en `PublicStorageKey` (`SoftDeleteFile.cs:35-44`, `SetFilePublication.cs:98-111`), y para un comprobante movido eso es la copia que enlaza el Excel: la evidencia del pago. `PublishFileHandler` copiaría desde un temporal que ya no existe. |
| D16 | Un comprobante ya movido no se puede adjuntar a otro pedido. `QuotationFileLookup` lo informa como no disponible (`IsAvailable = false` si es `PaymentProof` y ya tiene `PublicStorageKey`), y `OrderPaymentProofResolver` lo rechaza con el código que ya usa, `order.payment_proof.file_not_available`. | Sin esto, la copia al público sale de un temporal borrado y el request termina en 500. No se inventa un código: el archivo ya no está disponible para adjuntar. |
| D17 | Una migración de Quotations agrega índices sobre `order_payment_proofs.file_id` y `order_payment_proofs.public_storage_key`. | Las dos sondas nuevas consultan esa tabla en cada barrido. |
| D18 | `FileUserReferenceProbe` cuenta también los archivos `PaymentProof` de un usuario, no sólo los `User`. | El frontend sube los comprobantes con `ownerId` = el usuario. Sin esto, pasarlos a `PaymentProof` dejaría de retener a quien los subió, y `OrphanUserCleanupWorker` podría borrar un usuario que todavía es dueño de comprobantes. |
| D19 | Reemplazar el archivo de un comprobante (`UpdatedProofs[].NewFileId`) o quitarlo (`RemoveOrderPaymentProof`) **borra el archivo que el pedido deja de usar**, esté en `staging/` o ya movido al bucket público. En la misma transacción que el pedido, Quotations escribe `quotations.order.payment-proofs-detached.v1` con `tenantId`, `orderId` y, por cada archivo soltado, `fileId` y la `publicStorageKey` que tenía ese comprobante (null si no tenía copia); lo escribe también con `Quotations:PaymentProofs:PublicLinks=false`, y el borrado best-effort de la copia vieja después de guardar desaparece. El archivo de reemplazo cuenta como comprobante nuevo: entra en `quotations.order.payment-proofs-attached.v1` con su clave. Storage consume el evento con su inbox, en el mismo worker que el movimiento y después de él. Por cada archivo `PaymentProof` `Available` que ninguna `IFileReferenceProbe` retiene: borra el objeto público si tiene `PublicStorageKey`; si no, el de `staging/` y la copia que trae el evento; después, en un solo `SaveChanges`, `FileResource.PurgeDetachedPaymentProof`, la auditoría `storage.file.purged` con motivo `payment_proof_detached` y el inbox (para un mensaje con varios archivos, ver enmienda de implementación 4). Uno retenido, ya purgado o en otro estado se salta. De un archivo `User` sólo borra la copia de ese adjunto y audita `storage.public_object.purged` / `payment_proof_detached`. | Decidido por el owner (2026-09-16), después de rebasar la rama sobre `develop` (`528d368`), donde `1439897` agregó reemplazar y quitar comprobantes. Un comprobante mal cargado —quizá de otro cliente— no puede quedar expuesto, y en v2 la copia pública es la **única** copia de un comprobante movido: la borra Storage, que es dueño del archivo, con el mismo orden borrar → guardar de D9 y por la misma razón (no hay transacción entre las dos bases). |

Dos consecuencias que se aceptan y quedan documentadas:

- Con `Quotations:PaymentProofs:PublicLinks=false`, un `PaymentProof` adjunto no tiene clave pública, no genera evento y se queda en `staging/`. Se sigue descargando desde la app, y el barrido de la sección 3 lo respeta porque está referenciado. Producción tiene la opción encendida.
- Storage no tiene inbox: la sección 2 necesita una tabla de inbox nueva y su migración, con el mismo diseño que los inbox de los demás módulos.

Cómo convive D19 con lo demás:

- **La carrera.** Si un comprobante se adjunta y se reemplaza enseguida, Storage puede soltarlo antes de moverlo: la purga de un archivo sin mover borra su temporal y la copia que el adjunto alcanzó a hacer. `PaymentProofMoveWorker`, al llegar después, lo salta sin fallar —ya no está `Available`— y marca su mensaje.
- **La opción apagada.** El evento de retiro se escribe igual, con `publicStorageKey` null: el `PaymentProof` que el pedido suelta se borra de `staging/` en segundos, sin esperar al barrido de la sección 3.
- **Retenido.** Si otro comprobante (del mismo pedido o de otro) todavía usa el archivo, Quotations no lo incluye en el evento o la sonda lo retiene, y no se borra nada. Una copia que quede sin dueño por ese camino es un huérfano de `payment-proofs/`, y la recoge la reconciliación de la sección 4 (D12).
- **D15** no cambia: el procesador no pasa por los handlers de Storage, y al soltarse el archivo ya no lo referencia el pedido que lo soltó.
- **D16** tampoco: un archivo purgado no está `Available`, así que adjuntarlo de nuevo responde `order.payment_proof.file_not_available`. Para corregir, la asesora sube un archivo nuevo.
- **D18:** un comprobante purgado sigue reteniendo a quien lo subió, porque la sonda cuenta cualquier estado.
- **Revocar.** D19 acota «Nada se revoca» y el «Despublicar un comprobante o revocar su URL» de fuera de alcance: la URL de un comprobante reemplazado o quitado deja de abrir, a propósito. Un Excel ya enviado con esa URL muestra un enlace roto.
- **Los `User` (D13)** conservan su original privado. Lo único que cambia es quién borra la copia pública de un reemplazo (Storage, con reintento, en vez del borrado best-effort de Quotations) y que quitar un comprobante `User` ahora también borra la suya.

Pruebas de D19 (TDD, igual que el resto):

- unitaria: `FileResource.PurgeDetachedPaymentProof` acepta un `PaymentProof` `Available`, movido o no, y rechaza otro dueño u otro estado;
- unitaria: el evento sólo lleva los archivos que el pedido dejó de usar, y el de adjuntos suma los archivos de reemplazo con copia;
- integración (Quotations): reemplazar y quitar escriben el evento de retiro con el archivo y la clave viejos, también con la opción apagada; corregir sólo montos o un retiro rechazado no lo escriben; el evento de adjuntos incluye el archivo de reemplazo;
- integración de punta a punta: un `PaymentProof` movido que se reemplaza o se quita queda `Purged` y su copia pública se borra; quitar un comprobante `User` borra su copia y conserva el original;
- integración (Storage): purga el movido (borra el público) y el que sigue en `staging/` (borra el temporal y la copia), respeta el retenido, aplica cada mensaje una sola vez, no guarda nada si un borrado falla, conserva el original de un `User`, y el movimiento salta un comprobante purgado antes de moverse.

### Enmiendas de implementación (2026-09-16)

Entre Task 7 y Task 12, el ledger (`.superpowers/sdd/2026-09-16-comprobantes-publicos-v2/progress.md`, líneas «Ruling:» y las de ronda de arreglo) registró decisiones del owner y del controller que precisan D9, D11, D12, D15 y D19 más allá del texto de arriba. Quedan documentadas acá; ante cualquier discrepancia gana el código.

1. **Worker de movimiento (D9).** `PaymentProofMoveProcessor` separa los errores en dos. Un payload mal formado, o una entrada inválida (sin clave pública, de otro tenant, que no es `PaymentProof`, que no está `Available` o que ya tiene clave), se registra en `Warning` y se salta —el mensaje entero se marca procesado sin mover nada si el payload no se pudo leer—; sólo un error transitorio (R2, la base) hace que el mensaje vuelva en el tick siguiente. `PaymentProofMoveWorker` sólo corta su bucle cuando la cancelación viene del apagado del host (`stoppingToken.IsCancellationRequested`); cualquier otra cancelación (un timeout de R2) es un tick fallido más. La consulta de `FileResource` siempre filtra por `TenantId` en la base, nunca en memoria.
2. **Barrido de staging (D11).** `StagingCleanupProcessor` guarda cada comprobante no adjuntado con su propio `SaveChanges` (borrar objeto, `PurgeUnattachedPaymentProof`, auditoría y guardar), no en un solo lote: si uno falla, queda registrado y el barrido sigue con el siguiente sin perder lo ya borrado. Sin ninguna `IFileReferenceProbe` registrada, el barrido no purga nada —falla cerrado— y sólo deja un `Warning`.
3. **Guarda de D15.** `PaymentProofGuard.EnsureNotReferencedAsync` también falla cerrado: sin ninguna `IFileReferenceProbe` registrada, rechaza el borrado o la despublicación de un `PaymentProof` con `storage.file.invalid_state`, en vez de dejarlo pasar.
4. **Storage, D19.** Un mensaje de retiro con varios archivos no se guarda en un solo `SaveChanges`: `PaymentProofDetachProcessor` guarda la purga de cada archivo (borrado, `PurgeDetachedPaymentProof` y auditoría) antes de pasar al siguiente, y sólo la del último va junto con el inbox —los borrados de objetos no se pueden deshacer, así que cada purga tiene que quedar guardada antes del próximo borrado—. Un mensaje de un solo archivo, el caso de siempre, sigue en un solo `SaveChanges`. Sin ninguna `IFileReferenceProbe` registrada falla cerrado igual que el barrido: los mensajes pendientes quedan sin procesar, sin marcar el inbox, y sólo avisa un `Warning`.
5. **Quotations, D19.** `PaymentProofCopies.DetachedFrom` no filtra por `FileId`, sino por la `PublicStorageKey` de cada adjunto. Un candidato con clave se suelta salvo que esa misma clave siga en el pedido después del cambio, aunque otro comprobante del mismo pedido use el mismo archivo: cada adjunto tiene su propia clave pública, así que un reemplazo o un retiro se emite igual. Un candidato sin clave (con `Quotations:PaymentProofs:PublicLinks` apagada) sólo se suelta si el pedido ya no usa ese `FileId` en ningún comprobante. La firma quedó `DetachedFrom(IEnumerable<DetachedPaymentProof> candidates, IEnumerable<DetachedPaymentProof> remaining)`, no contra una lista de `FileId` restantes. `OrderPaymentProofEventPublisher` siempre serializa `publicStorageKey` en el payload, `null` incluido; nunca lo omite.
6. **Reconciliación (D12).** `PaymentProofOrphanCleanupWorker` no espera un `IntervalHours` completo para su primera corrida: la primera llega un rato después de arrancar (constante interna `InitialDelay`, cinco minutos por defecto; sólo las pruebas la cambian), y las siguientes cada `IntervalHours`. `PaymentProofOrphanCleanupProcessor` audita y guarda cada objeto borrado por separado y sigue con el siguiente si uno falla; sin ninguna `IPublicObjectReferenceProbe` registrada no borra nada —falla cerrado, ni siquiera lista el bucket—. Varias réplicas pueden correr la reconciliación a la vez: borrar un objeto que ya no existe no falla, y en producción `DryRun` arranca en `true`.

## Fuera de alcance

- Migrar los comprobantes v1 (`User`) al esquema v2.
- Cambiar el flujo de imágenes de producto u otros archivos de Storage.
- Una API o `CronJob` para disparar la limpieza, y el botón en el panel de operadores.
- Despublicar un comprobante o revocar su URL.
- Configuración por tenant.
