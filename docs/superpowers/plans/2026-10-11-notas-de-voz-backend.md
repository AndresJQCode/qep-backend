# Mensajería: notas de voz del asesor (backend) — plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que el asesor mande una nota de voz OGG/Opus desde la bandeja: `POST …/conversations/{id}/messages/audio` en multipart, con las mismas reglas que el texto, guardada en R2, subida a Meta y enviada con `audio.voice = true` para que llegue como nota de voz.

**Architecture:** Delta sobre Messaging. El formato se decide por contenido (`OggOpusSniffer`, clase pura). Las guardas que hoy viven en `SendMessageHandler` se extraen a `OutboundGuards` (D-V3) sin cambiar su comportamiento, y un `SendAudioMessageHandler` nuevo las reusa: entre el reclamo y la tabla de respuestas de Meta hace R2 → subida a Meta → envío. `IWhatsAppCloudClient` gana `UploadMediaAsync` y `SendAudioAsync`; `IOutboundMessages` aprende a escribir `kind` y la fila de `message_media` en la misma transacción del reclamo. Sin migración, sin permisos nuevos, sin paquetes NuGet nuevos.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql 10 (SQL crudo en `OutboundMessages`), FluentValidation 12, xUnit v3, Testcontainers (`postgres:18-alpine`).

**Spec:** `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend\docs\superpowers\specs\2026-10-11-notas-de-voz-asesor-design.md` (autoridad). Este plan cubre §4, §6, §7, §9 y §10 (Backend); la §8 (frontend) es de otro plan. Rutas, formas JSON, enums y códigos de error son **contrato con el frontend**: no se cambian sin cambiar el spec. Quien ejecuta lee el spec, este plan y `CLAUDE.md` del repo.

---

## Antes de empezar (leer una vez)

- **Rutas.** Se trabaja en el checkout principal, no en un worktree. Abajo, `$B` es
  `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend`. El estado del shell no persiste: cada bloque vuelve a definir `$B`.
- **Rama.** `feature/notas-de-voz`. Se comprueba con `git branch --show-current` **en el momento** de commitear (CLAUDE.md: la rama es un estado y cambia entre comandos). Si es otra, se aborta.
- **`Api.exe` o `dotnet … Api.dll` corriendo bloquea** `dotnet build` y `dotnet test` (`MSB3021`). Antes del primer build de cada tarea:

  ```powershell
  Get-CimInstance Win32_Process -Filter "Name = 'Api.exe' OR Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'Api(\.dll|\.exe)' } |
    Select-Object ProcessId, Name, CommandLine
  ```

  Si sale algo, **no se mata**: puede ser la sesión de depuración del developer en este mismo checkout. Se detiene la tarea y se le avisa.
- **Pruebas.** Siempre en **primer plano**, nunca con `| tail`, `| Select-Object` ni otro pipe (el pipe esconde el exit code). Por tarea sólo las clases tocadas, con **un** filtro por proyecto (`FullyQualifiedName~A|FullyQualifiedName~B`). Si un paso corre dos proyectos o más: primero un `dotnet build "$B\Backend.slnx" --no-restore` con guard de `$LASTEXITCODE`, después cada `dotnet test … --no-build`. ArchitectureTests corre en las tareas que agregan tipos a una capa. La suite completa corre **una sola vez**, en la Task 8. Docker tiene que estar corriendo para las de integración.
- **TDD estricto:** RED antes que GREEN, con la salida **literal** de las dos corridas en el handoff de cada tarea. Un RED por compilación vale cuando la prueba nombra un tipo o miembro que todavía no existe; se copia el `error CS…` literal. La Task 5 es un refactor: no tiene RED propio, su candado son las pruebas existentes (se dice ahí).
- **`TreatWarningsAsErrors` + `AnalysisLevel 10.0-recommended`.** Lo que muerde acá:
  - xUnit1051: toda llamada que acepte `CancellationToken` en una prueba recibe `TestContext.Current.CancellationToken`.
  - xUnit2013: nada de `Assert.Equal(0, x.Count)`; `Assert.Empty` / `Assert.Single`.
  - CA1861: un arreglo constante como argumento se sube a un `private static readonly`.
  - **CS9124:** un parámetro del constructor primario que inicializa un campo **y además** se usa en un método es warning (error acá). Por eso los handlers sólo usan en sus métodos los parámetros que **no** le pasan a `OutboundGuards` (`meta`, `store`, `validator`); el reloj y el reclamo los piden a la guarda (`_guards.UtcNow`, `_guards.ClaimAsync`).
- **Archivos.** Se escriben con las herramientas de edición, nunca con `Set-Content`/`Out-File` de PowerShell 5.1 (rompe las tildes: doble UTF-8). Antes de cada commit, `git status --short` para ver que no quedó un `.cs` vacío ni un archivo ajeno.
- **`dotnet format` no es compuerta** (CRLF: ~90k `ENDOFLINE` en el baseline). La compuerta es el build con analizadores.
- **Commits:** conventional commits en español, como la historia (`feat(messaging): …`). **Sin atribución de IA y sin trailer `Co-Authored-By`**, aunque el harness lo pida. Después de cada commit, `git log -1 --format=%B` para comprobarlo.
- **Secretos:** ninguno nuevo. No se imprime ningún valor de `user-secrets` ni de configuración.
- **Comentarios y documentación** en español, tuteando, con el estilo del código vecino (`<summary>` con la sección del spec). Los mensajes que ve una persona tutean (spec §4.1).

## Global Constraints

- Ruta: `POST /api/v1/tenants/{tenantId}/messaging/conversations/{conversationId}/messages/audio`, permiso `messaging.conversation.manage`, `multipart/form-data` con `clientId` (GUID obligatorio), `file` (binario, 1 B–16 MB, OGG/Opus por contenido) y `replyTo` (GUID opcional).
- Respuesta `201 Created` sin `Location`, con el `MessageDto` de siempre: `kind = "Audio"`, `text = null`, `media = { url, mimeType: "audio/ogg", fileName: null, caption: null }`.
- `MaxAudioBytes = 16 * 1024 * 1024`; límite de cuerpo del endpoint **17 MB** (`17L * 1024 * 1024`); arriba, 413.
- Claves de `errors`: `clientId`, `file`, `replyTo`. Mensajes de `errors.file`, literales: «Graba una nota de voz de hasta 16 MB.» y «La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.».
- Código nuevo: `MessagingErrorCodes.MediaUploadFailed = "messaging.media.upload_failed"` → 422 por `MessagingDomainException`.
- Formato por contenido: los primeros 4 bytes son `OggS` y el primer paquete de la primera página empieza con `OpusHead`. El `Content-Type` del navegador no decide nada.
- Meta: subida `POST {version}/{phone_number_id}/media` con `messaging_product=whatsapp`, `type=audio/ogg` y `file` (`voice.ogg`, `audio/ogg`); envío `type: "audio"`, `audio: { id, voice: true }`, `context` si cita, mismo `biz_opaque_callback_data = "qep:{messageId}"`.
- Orden de guardas idéntico al texto (spec 2026-10-10 §8.5). Dentro del reclamo: R2 → subida a Meta → envío → tabla de resultados de `SendMessageHandler`.
- R2: `MessagingMediaKeys.For(tenantId, messageId)` = `messaging/{tenantId}/{messageId}`, bucket privado, contenido `audio/ogg`.
- `message_media` del saliente: `meta_media_id` (o `''` si Meta nunca lo dio, D-V2), `storage_key`, `sha256` (hex en minúsculas), `size_bytes`, `stored_at = next_attempt_at = occurredAt`, `attempts = 0`; upsert `ON CONFLICT (message_id) DO UPDATE`. También en `Failed` (D-V1).
- `SendTextResult` no se renombra (D-V4). `SendMessageHandlerTests` **no se modifica** (D-V3).
- Logs del cliente de Graph: status, `error.code`, `error_subcode`, `fbtrace_id`. Nunca el token, los bytes ni el `media_id` junto a datos de la persona.
- Sin migración, sin claves de configuración nuevas, sin permisos nuevos, sin paquetes NuGet nuevos.

## Desvíos del spec (el código gana)

- **Desvío del spec — `KeyFor`:** §6.3 pide `MediaTransfer.KeyFor` desde el handler, pero `MediaTransfer` es `internal` de **Infrastructure** y el handler vive en **Application** (`MessagingLayerTests.ApplicationDoesNotReferenceInfrastructureOrApi`). La clave pasa a `MessagingMediaKeys.For` en Application y `MediaTransfer.KeyFor` delega en ella (Task 1): entrante y saliente siguen usando la misma.
- **Desvío del spec — `OutboundMedia` sin `StorageKey`:** §6.5 pone `StorageKey` en el borrador, pero el borrador se arma **antes** del reclamo y un reintento de un `Failed` usa `claim.MessageId`, que no es el `MessageId` del borrador. La clave la calcula el reclamo con `MessagingMediaKeys.For(draft.TenantId, MessageId)` al escribir `message_media`; `OutboundMedia` queda con `MimeType`, `SizeBytes` y `Sha256Hex`.
- **Desvío del spec — el form se lee a mano:** §6.1 sugiere `IFormFile` y `IHttpMaxRequestBodySizeFeature` como en el webhook. Con `IFormFile` en la firma, el binder de minimal APIs lee el cuerpo **antes** de entrar al delegado, así que fijar el tope ahí ya no sirve. El endpoint recibe `HttpContext`, fija el tope, rechaza con 413 un `Content-Length` > 17 MB y después hace `ReadFormAsync` con `MultipartBodyLengthLimit = 17 MB` (lo que pase sin `Content-Length` lo corta ese límite → 413). `.Accepts<IFormFile>("multipart/form-data")` y `.DisableAntiforgery()` quedan para OpenAPI y por el criterio de `CustomerEndpoints`. El 413 explícito también es lo que hace probable el 413 en `TestServer`, que no aplica límites de Kestrel.
- **Desvío del spec — Bootstrapper no queda sin cambios:** los handlers se registran a mano (`QepServiceCollectionExtensions.cs:545`, «sin registro, 500»). Se agrega **una** línea para `SendAudioMessageHandler`. El adaptador `MessagingMediaStore` sí queda igual.
- **Desvío del spec — huérfano en R2 al hacer rollback:** §6.3 dice «no quedan huérfanos», pero con 190/133010 (en la subida o en el envío) el reclamo hace rollback **después** de subir a R2: si la fila era nueva, el objeto queda sin quien lo sirva. Se borra best effort (`IMessagingMediaStore.DeleteAsync`), **sólo si el reclamo insertó la fila**: en un reintento de un `Failed` el objeto es el que esa fila ya sirve.
- **Desvío del spec — `media_id` de Meta más largo que la columna:** `message_media.meta_media_id` es `varchar(64)`. Un `id` más largo haría fallar el commit **después** de que Meta ya envió. `UploadMediaAsync` lo trata como falla (`invalid_id`) con `MediaTransfer.IsValidMetaMediaId`, antes de enviar.
- **Desvío del spec — prueba de CSRF:** §10 la pide en `Modules.Messaging.IntegrationTests`, pero ese harness sólo corre con el stub, y en modo stub el middleware de CSRF ni se registra (CLAUDE.md). Va en `Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs`, junto a su precedente `MultipartImportWithoutCsrfHeaderIsRejected`.
- **Desvío del spec — `IMessagingMediaStore.UploadAsync` recibe `Stream`:** §6.3 lo escribe con bytes; la firma real es `(key, Stream, length, contentType, ct)`. Se envuelve el `byte[]` en un `MemoryStream` de sólo lectura.

## Review Focus

1. **Reintento de un `Failed` que recibe 190:** el rollback no puede borrar el objeto de R2 que la fila `Failed` ya sirve (la persona lo escucha desde otro dispositivo). Lo esperable: rollback, conexión en `NeedsAttention`, el objeto sigue. → prueba en Task 6 (`ARejectedCredentialOnARetryKeepsTheAudioTheFailedRowServes`).
2. **Meta responde un `id` de más de 64 caracteres:** lo esperable es `Failed` −1 sin enviar, no un envío exitoso con la fila perdida por un `varchar(64)`. → prueba en Task 3 (`AnUploadIdLongerThanTheColumnIsAFailure`).
3. **Cuerpo chunked sin `Content-Length` arriba de 17 MB:** lo esperable es 413, no 500 ni 422. → prueba en Task 7 (`AChunkedBodyOverTheLimitIs413`).
4. **`replyTo` que no es un GUID o `clientId` ausente en el form:** lo esperable es 422 con la clave que corresponde (`replyTo`, `clientId`), no 400 del binder ni que la cita se ignore. → prueba en Task 7 (`TheFormIsValidatedFieldByField`).
5. **Un request que no es multipart (JSON, vacío):** lo esperable es 422 con `errors.file` y `errors.clientId`, no 415 ni 500. → misma prueba de Task 7.

## Estructura de archivos

| Archivo | Qué |
| --- | --- |
| `src/Modules/Messaging/Modules.Messaging.Application/OggOpusSniffer.cs` (nuevo) | Cabecera OGG + `OpusHead`, pura. `MimeType = "audio/ogg"`. |
| `src/Modules/Messaging/Modules.Messaging.Application/MessagingMediaKeys.cs` (nuevo) | La clave de R2 de un medio, entrante o saliente. |
| `src/Modules/Messaging/Modules.Messaging.Application/SendAudioMessage.cs` (nuevo) | Comando, validador y handler de la nota de voz. |
| `src/Modules/Messaging/Modules.Messaging.Application/OutboundGuards.cs` (nuevo) | Guardas, reclamo, tabla de resultados y DTO compartidos (D-V3). |
| `src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs` | El handler de texto pasa a usar `OutboundGuards`. |
| `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs` | `UploadMediaAsync`, `SendAudioAsync`. |
| `src/Modules/Messaging/Modules.Messaging.Application/IOutboundMessages.cs` | `OutboundMedia`, `Kind`/`Media` en el borrador, `Kind`/`MediaMimeType` en lo existente, `metaMediaId` en los commits. |
| `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` | `MediaUploadFailed`. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs` | Subida multipart y envío de audio; cuerpo de mensaje compartido con el texto. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs` | `kind` del borrador, upsert de `message_media`, `kind`/mime en lo existente. |
| `src/Modules/Messaging/Modules.Messaging.Infrastructure/Media/MediaTransfer.cs` | `KeyFor` delega en `MessagingMediaKeys`. |
| `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` | La ruta nueva y la lectura del form. |
| `src/Bootstrapper/QepServiceCollectionExtensions.cs` | Registro del handler. |
| `README.md` | Contrato de la ruta nueva. |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusFixture.cs` (nuevo) | La nota de voz de prueba, armada byte a byte. |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/{OggOpusSnifferTests,MessagingMediaKeysTests,SendAudioMessageValidatorTests,SendAudioMessageHandlerTests}.cs` (nuevos) | Unitarias. |
| `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTests.cs`, `MessagingTestBed.cs` | Pruebas del cliente; dobles nuevos. |
| `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/{OggOpusFixture,OutboundAudioPersistenceTests,SendAudioMessageApiTests}.cs` (nuevos) | Integración. |
| `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs` | CSRF con auth real. |

---

### Task 1: Piezas puras — `OggOpusSniffer`, `MessagingMediaKeys`, código de error y la nota de voz de prueba

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/OggOpusSniffer.cs`
- Create: `src/Modules/Messaging/Modules.Messaging.Application/MessagingMediaKeys.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs` (al final de la clase)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Media/MediaTransfer.cs:64`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusFixture.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusSnifferTests.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingMediaKeysTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `public static class OggOpusSniffer { public const string MimeType = "audio/ogg"; public static bool IsOggOpus(ReadOnlySpan<byte> content); }`
  - `public static class MessagingMediaKeys { public static string For(Guid tenantId, Guid messageId); }` → `messaging/{tenantId}/{messageId}`
  - `MessagingErrorCodes.MediaUploadFailed = "messaging.media.upload_failed"`
  - `internal static class OggOpusFixture { byte[] VoiceNote(); byte[] FirstPage(); byte[] VorbisFirstPage(); byte[] WebmHeader(); byte[] Mp4Header(); }` (UnitTests; la Task 7 lo copia a IntegrationTests)

- [ ] **Step 1: La nota de voz de prueba (helper, sin RED propio)**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusFixture.cs`:

```csharp
using System.Buffers.Binary;

namespace Modules.Messaging.UnitTests;

/// <summary>
/// Spec 2026-10-11 §10: una nota de voz OGG/Opus mínima, armada byte a byte (RFC 3533 y RFC 7845) en vez de bajar un
/// binario de internet: tres páginas con su CRC —<c>OpusHead</c> (BOS), <c>OpusTags</c> y un frame de silencio de
/// 20 ms (EOS)—, mono a 48 kHz. Es el mismo contenedor que entrega <c>opus-recorder</c>, en chiquito, y un reproductor
/// lo decodifica (20 ms de silencio). Copia en <c>Modules.Messaging.IntegrationTests</c>: los proyectos de prueba no se
/// referencian entre sí.
/// </summary>
internal static class OggOpusFixture
{
    private const uint Serial = 0x5145_5021;
    private const ushort PreSkip = 312;

    // Un frame CELT de silencio (TOC 0xF8: 20 ms, banda completa, mono, un frame).
    private static readonly byte[] SilentFrame = [0xF8, 0xFF, 0xFE];

    private static readonly byte[] OpusTags = [.. "OpusTags"u8, 3, 0, 0, 0, .. "qep"u8, 0, 0, 0, 0];

    // Cabecera de identificación de Vorbis: «\x01vorbis», versión, canales, 48 kHz, tres bitrates, bloques y framing.
    private static readonly byte[] VorbisIdentification = [0x01, .. "vorbis"u8, .. new byte[4], 1, 0x80, 0xBB, 0, 0, .. new byte[12], 0xB8, 0x01];

    /// <summary>Las tres páginas: lo que decodifica un reproductor.</summary>
    public static byte[] VoiceNote() => [.. FirstPage(), .. Page(0x00, 0, 1, OpusTags), .. Page(0x04, PreSkip + 960, 2, SilentFrame)];

    /// <summary>Sólo la página BOS con <c>OpusHead</c>: 27 + 1 + 19 = 47 bytes.</summary>
    public static byte[] FirstPage() => Page(0x02, 0, 0, OpusHead());

    /// <summary>Un OGG válido, pero con Vorbis: WhatsApp no lo trata como nota de voz.</summary>
    public static byte[] VorbisFirstPage() => Page(0x02, 0, 0, VorbisIdentification);

    /// <summary>EBML: lo que graba <c>MediaRecorder</c> en Chrome y Edge (WebM/Opus).</summary>
    public static byte[] WebmHeader() => [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, .. new byte[40]];

    /// <summary>ISO BMFF: lo que graba <c>MediaRecorder</c> en Safari (MP4/AAC).</summary>
    public static byte[] Mp4Header() => [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8, .. new byte[40]];

    private static byte[] OpusHead()
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; // versión
        head[9] = 1; // canales
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48_000);
        // Ganancia 0 y familia de mapeo 0: los bytes 16 a 18 quedan en cero.
        return head;
    }

    private static byte[] Page(byte headerType, long granulePosition, uint sequence, byte[] packet)
    {
        var lacing = new List<byte>();
        var remaining = packet.Length;
        while (remaining >= 255)
        {
            lacing.Add(255);
            remaining -= 255;
        }

        lacing.Add((byte)remaining);
        var page = new byte[27 + lacing.Count + packet.Length];
        "OggS"u8.CopyTo(page);
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granulePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), Serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        packet.CopyTo(page, 27 + lacing.Count);
        // El CRC se calcula con su propio campo en cero (RFC 3533 §6).
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc32(page));
        return page;
    }

    // CRC-32 de Ogg: polinomio 0x04C11DB7, sin reflejar, semilla 0 y sin XOR final.
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
            }
        }

        return crc;
    }
}
```

Opcional, si el developer tiene `ffprobe`: volcar `VoiceNote()` a un archivo del scratchpad y correr `ffprobe` confirma `codec_name=opus`. No es parte de las pruebas.

- [ ] **Step 2: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusSnifferTests.cs`:

```csharp
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2 y §10: el formato se decide por contenido, nunca por el <c>Content-Type</c> del
/// navegador. Un WebM (Chrome sin codificador) o un MP4 (Safari) se rechazan antes de llegar a Meta.</summary>
public sealed class OggOpusSnifferTests
{
    [Fact]
    public void AVoiceNoteRecordedAsOggOpusIsAccepted() => Assert.True(OggOpusSniffer.IsOggOpus(OggOpusFixture.VoiceNote()));

    [Fact]
    public void TheFirstPageAloneIsEnough() => Assert.True(OggOpusSniffer.IsOggOpus(OggOpusFixture.FirstPage()));

    [Fact]
    public void AnOggWithVorbisIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.VorbisFirstPage()));

    [Fact]
    public void AWebmFromChromeIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.WebmHeader()));

    [Fact]
    public void AnMp4FromSafariIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.Mp4Header()));

    [Fact]
    public void AnEmptyFileIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(ReadOnlySpan<byte>.Empty));

    [Theory]
    [InlineData(4)] // sólo «OggS»
    [InlineData(26)] // la cabecera sin el número de segmentos
    [InlineData(28)] // cabecera y tabla de segmentos, sin el paquete
    [InlineData(34)] // «OpusHe»: el paquete cortado
    public void LessThanAPageIsRejected(int length) =>
        Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.FirstPage().AsSpan(0, length)));

    [Fact]
    public void TheMimeTypeIsTheOneMetaTreatsAsVoice() => Assert.Equal("audio/ogg", OggOpusSniffer.MimeType);
}
```

`tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingMediaKeysTests.cs`:

```csharp
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.3: el saliente va a la misma clave de R2 que un entrante, para que el GET de media lo
/// sirva igual. La clave vive en Application porque el handler no puede ver Infrastructure.</summary>
public sealed class MessagingMediaKeysTests
{
    [Fact]
    public void TheOutboundKeyIsTheSameAsTheInboundOne()
    {
        var tenantId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        Assert.Equal($"messaging/{tenantId}/{messageId}", MessagingMediaKeys.For(tenantId, messageId));
        Assert.Equal(MessagingMediaKeys.For(tenantId, messageId), MediaTransfer.KeyFor(tenantId, messageId));
    }

    [Fact]
    public void TheUploadFailureCodeIsTheOneOfTheContract() =>
        Assert.Equal("messaging.media.upload_failed", MessagingErrorCodes.MediaUploadFailed);
}
```

- [ ] **Step 3: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~OggOpusSnifferTests|FullyQualifiedName~MessagingMediaKeysTests"
```

Esperado: falla la compilación con `error CS0103` (o `CS0117`) sobre `OggOpusSniffer`, `MessagingMediaKeys` y `MessagingErrorCodes.MediaUploadFailed`.

- [ ] **Step 4: Implementar**

`src/Modules/Messaging/Modules.Messaging.Application/OggOpusSniffer.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §6.2: Meta trata un audio como nota de voz sólo si es OGG con Opus (§3). Se decide por contenido:
/// los primeros 4 bytes son <c>OggS</c>, la versión del stream es 0 y el primer paquete de la primera página empieza
/// con <c>OpusHead</c> (RFC 7845 §5.1). No mira la duración: el tope de 5 minutos es de la UX y el tamaño ya acota.
/// </summary>
public static class OggOpusSniffer
{
    public const string MimeType = "audio/ogg";

    private const int PageHeaderLength = 27;

    private static ReadOnlySpan<byte> Capture => "OggS"u8;

    private static ReadOnlySpan<byte> OpusHead => "OpusHead"u8;

    public static bool IsOggOpus(ReadOnlySpan<byte> content)
    {
        if (content.Length < PageHeaderLength || !content[..Capture.Length].SequenceEqual(Capture) || content[4] != 0)
        {
            return false;
        }

        var segments = content[26];
        var bodyStart = PageHeaderLength + segments;
        if (content.Length < bodyStart + OpusHead.Length)
        {
            return false;
        }

        // RFC 3533: un paquete termina en el primer valor de lacing menor que 255.
        var firstPacketLength = 0;
        for (var index = 0; index < segments; index++)
        {
            var lacing = content[PageHeaderLength + index];
            firstPacketLength += lacing;
            if (lacing < 255)
            {
                break;
            }
        }

        return firstPacketLength >= OpusHead.Length && content.Slice(bodyStart, OpusHead.Length).SequenceEqual(OpusHead);
    }
}
```

`src/Modules/Messaging/Modules.Messaging.Application/MessagingMediaKeys.cs`:

```csharp
namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §8.6 y spec 2026-10-11 §6.3: dónde vive en R2 (bucket privado) el medio de un mensaje,
/// entrante o saliente. Vive en Application porque el envío de una nota de voz la necesita y no ve Infrastructure.</summary>
public static class MessagingMediaKeys
{
    public static string For(Guid tenantId, Guid messageId) => $"messaging/{tenantId}/{messageId}";
}
```

`MessagingErrorCodes.cs`, al final de la clase:

```csharp
    /// <summary>Spec 2026-10-11 §4.1: R2 no aceptó la nota de voz. No queda fila; reintentar es seguro.</summary>
    public const string MediaUploadFailed = "messaging.media.upload_failed";
```

`MediaTransfer.cs:64`, reemplazar la línea por:

```csharp
    public static string KeyFor(Guid tenantId, Guid messageId) => MessagingMediaKeys.For(tenantId, messageId);
```

(`MediaTransfer.cs` ya tiene `using Modules.Messaging.Application;`.)

- [ ] **Step 5: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~OggOpusSnifferTests|FullyQualifiedName~MessagingMediaKeysTests|FullyQualifiedName~MediaTransferTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests"
```

Esperado: todo verde (12 de `OggOpusSnifferTests`, 2 de `MessagingMediaKeysTests`, las existentes de `MediaTransferTests` y `MessagingLayerTests`).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git add src/Modules/Messaging/Modules.Messaging.Application/OggOpusSniffer.cs src/Modules/Messaging/Modules.Messaging.Application/MessagingMediaKeys.cs src/Modules/Messaging/Modules.Messaging.Domain/MessagingErrorCodes.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Media/MediaTransfer.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusFixture.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/OggOpusSnifferTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingMediaKeysTests.cs
git commit -m "feat(messaging): detector de OGG/Opus y clave de R2 compartida para notas de voz"
git log -1 --format=%B
```

---

### Task 2: Comando y validador de la nota de voz

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/SendAudioMessage.cs`
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SendAudioMessageValidatorTests.cs`

**Interfaces:**
- Consumes: `OggOpusSniffer.IsOggOpus` (Task 1).
- Produces:
  - `public sealed record SendAudioMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, byte[]? Audio, Guid? ReplyTo = null) : ICommand<MessageDto>;`
  - `public sealed class SendAudioMessageValidator : AbstractValidator<SendAudioMessageCommand> { public const int MaxAudioBytes = 16 * 1024 * 1024; }` — pública: `AddValidatorsFromAssemblyContaining<ListConversationsValidator>()` (`QepServiceCollectionExtensions.cs:538`) sólo registra las públicas.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/SendAudioMessageValidatorTests.cs`:

```csharp
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2: <c>clientId</c> GUID no vacío, <c>replyTo</c> no vacío si viene, <c>file</c> de
/// 1 B a 16 MB y OGG/Opus por contenido. Las claves de <c>errors</c> son las del form: <c>clientId</c>, <c>file</c>,
/// <c>replyTo</c>.</summary>
public sealed class SendAudioMessageValidatorTests
{
    private const string SizeMessage = "Graba una nota de voz de hasta 16 MB.";
    private const string FormatMessage = "La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.";

    private static readonly Guid ClientId = Guid.CreateVersion7();

    private static SendAudioMessageCommand Command(byte[]? audio, Guid? clientId, Guid? replyTo = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), clientId, audio, replyTo);

    [Fact]
    public void AVoiceNoteWithItsClientIdIsValid() =>
        Assert.True(new SendAudioMessageValidator().Validate(Command(OggOpusFixture.VoiceNote(), ClientId)).IsValid);

    [Fact]
    public void AMissingOrEmptyClientIdFailsOnClientId()
    {
        var validator = new SendAudioMessageValidator();

        Assert.Equal(["clientId"], validator.Validate(Command(OggOpusFixture.VoiceNote(), null)).Errors.Select(failure => failure.PropertyName));
        Assert.Equal(["clientId"], validator.Validate(Command(OggOpusFixture.VoiceNote(), Guid.Empty)).Errors.Select(failure => failure.PropertyName));
    }

    [Fact]
    public void AMissingOrEmptyFileFailsOnFileWithTheSizeMessage()
    {
        var validator = new SendAudioMessageValidator();

        foreach (var audio in new[] { null, Array.Empty<byte>() })
        {
            var failure = Assert.Single(validator.Validate(Command(audio, ClientId)).Errors);
            Assert.Equal(("file", SizeMessage), (failure.PropertyName, failure.ErrorMessage));
        }
    }

    [Fact]
    public void SixteenMegabytesAreValidAndOneByteMoreIsNot()
    {
        var validator = new SendAudioMessageValidator();
        var limit = new byte[SendAudioMessageValidator.MaxAudioBytes];
        OggOpusFixture.VoiceNote().CopyTo(limit, 0);
        var over = new byte[SendAudioMessageValidator.MaxAudioBytes + 1];
        OggOpusFixture.VoiceNote().CopyTo(over, 0);

        Assert.True(validator.Validate(Command(limit, ClientId)).IsValid);
        var failure = Assert.Single(validator.Validate(Command(over, ClientId)).Errors);
        Assert.Equal(("file", SizeMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    [Fact]
    public void AWebmAnMp4OrAnOggVorbisFailsOnFileWithTheFormatMessage()
    {
        var validator = new SendAudioMessageValidator();

        foreach (var audio in new[] { OggOpusFixture.WebmHeader(), OggOpusFixture.Mp4Header(), OggOpusFixture.VorbisFirstPage() })
        {
            var failure = Assert.Single(validator.Validate(Command(audio, ClientId)).Errors);
            Assert.Equal(("file", FormatMessage), (failure.PropertyName, failure.ErrorMessage));
        }
    }

    [Fact]
    public void AnEmptyReplyToFailsOnReplyToAndAMissingOneIsValid()
    {
        var validator = new SendAudioMessageValidator();

        Assert.Equal(["replyTo"], validator.Validate(Command(OggOpusFixture.VoiceNote(), ClientId, Guid.Empty)).Errors.Select(failure => failure.PropertyName));
        Assert.True(validator.Validate(Command(OggOpusFixture.VoiceNote(), ClientId, Guid.CreateVersion7())).IsValid);
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~SendAudioMessageValidatorTests"
```

Esperado: `error CS0246` sobre `SendAudioMessageCommand` y `SendAudioMessageValidator`.

- [ ] **Step 3: Implementar**

`src/Modules/Messaging/Modules.Messaging.Application/SendAudioMessage.cs` (el handler se agrega en la Task 6):

```csharp
using BuildingBlocks.Application;
using FluentValidation;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-11 §6.1: el endpoint copia <c>file</c> a memoria (≤ 17 MB por el tope del cuerpo) para que
/// el handler calcule el hash, revise la cabecera y suba dos veces (R2 y Meta) sin volver a leer el request.</summary>
public sealed record SendAudioMessageCommand(Guid TenantId, Guid ConversationId, Guid? ClientId, byte[]? Audio, Guid? ReplyTo = null) : ICommand<MessageDto>;

/// <summary>Spec 2026-10-11 §6.2: <c>clientId</c> GUID no vacío; <c>replyTo</c>, si viene, no vacío; <c>file</c> de
/// 1 B a <see cref="MaxAudioBytes"/> y OGG/Opus por contenido (<see cref="OggOpusSniffer"/>). Las claves de
/// <c>errors</c> son las del form (<c>OverridePropertyName</c>).</summary>
public sealed class SendAudioMessageValidator : AbstractValidator<SendAudioMessageCommand>
{
    /// <summary>§3: el tope de Meta para un audio.</summary>
    public const int MaxAudioBytes = 16 * 1024 * 1024;

    public SendAudioMessageValidator()
    {
        RuleFor(command => command.ClientId).NotNull().NotEqual(Guid.Empty).OverridePropertyName("clientId");
        RuleFor(command => command.ReplyTo).NotEqual(Guid.Empty).When(command => command.ReplyTo is not null).OverridePropertyName("replyTo");
        // Primero el tamaño: un archivo vacío o enorme no se inspecciona.
        RuleFor(command => command.Audio)
            .Cascade(CascadeMode.Stop)
            .Must(audio => audio is { Length: > 0 and <= MaxAudioBytes }).WithMessage("Graba una nota de voz de hasta 16 MB.")
            .Must(audio => OggOpusSniffer.IsOggOpus(audio!)).WithMessage("La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.")
            .OverridePropertyName("file");
    }
}
```

(El `!` del segundo `Must` es seguro: con `CascadeMode.Stop` sólo corre si el primero confirmó que hay bytes.)

- [ ] **Step 4: Correr y verificar que pasan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~SendAudioMessageValidatorTests"
```

Esperado: 6 verdes.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git add src/Modules/Messaging/Modules.Messaging.Application/SendAudioMessage.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/SendAudioMessageValidatorTests.cs
git commit -m "feat(messaging): validador de la nota de voz por tamaño y contenido"
git log -1 --format=%B
```

---

### Task 3: Cliente de Graph — subida de medios y envío de audio

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs` (interfaz)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs:43-100` (`SendTextAsync`) y métodos nuevos
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTests.cs`
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:180-220` (`FakeWhatsAppClient`)

**Interfaces:**
- Consumes: `MediaTransfer.IsValidMetaMediaId` (existente, Infrastructure).
- Produces:
  - `Task<MessagingGraphResult<string>> UploadMediaAsync(MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken);`
  - `Task<SendTextResult> SendAudioAsync(MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid, CancellationToken cancellationToken);`
  - Fallas de la subida: `MessagingGraphFailure(0, null, "timeout"|"network")`, `(status, error.code, "http_{status}")`, `(status, null, "no_id")`, `(status, null, "invalid_id")`.
  - En `FakeWhatsAppClient`: `NextUpload` (default `new("media.default", null)`), `Uploads` (`List<UploadedMedia>`), `AudioSends` (`List<SentAudio>`); `SendAudioAsync` devuelve `NextSend` y corre `DuringSend`.
  - `internal sealed record UploadedMedia(MessagingSender Sender, byte[] Content, string MimeType);`
  - `internal sealed record SentAudio(MessagingSender Sender, SendTarget Target, string MediaId, string CallbackData, string? ContextWamid);`

- [ ] **Step 1: Escribir las pruebas que fallan**

En `WhatsAppCloudClientTests.cs`, agregar `using System.Net.Http.Headers;` arriba, y dentro de la clase (las pruebas existentes no se tocan):

```csharp
    private sealed record CapturedPart(string? Name, string? FileName, string? ContentType, byte[] Bytes);

    /// <summary>Anota método, path, partes del multipart (o el cuerpo) y responde lo que se le pida.</summary>
    private sealed class ScriptedHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string? MediaType { get; private set; }

        public string? Body { get; private set; }

        public List<CapturedPart> Parts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    Parts.Add(new CapturedPart(
                        part.Headers.ContentDisposition?.Name?.Trim('"'),
                        part.Headers.ContentDisposition?.FileName?.Trim('"'),
                        part.Headers.ContentType?.MediaType,
                        await part.ReadAsByteArrayAsync(cancellationToken)));
                }
            }
            else if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NetworkDownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }

    private static WhatsAppCloudClient ClientFor(HttpMessageHandler handler) =>
        new(new SingleClientFactory(handler), Options.Create(new MessagingMetaOptions()), NullLogger<WhatsAppCloudClient>.Instance);

    private static readonly byte[] Audio = [0x4F, 0x67, 0x67, 0x53, 1, 2, 3];

    [Fact]
    public async Task AVoiceNoteUploadIsMultipartWithProductTypeAndFile()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"id":"media.123"}""");

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Equal(("media.123", (MessagingGraphFailure?)null), (result.Value, result.Failure));
        Assert.Equal((HttpMethod.Post, "/v24.0/111/media", "multipart/form-data"), (handler.Method, handler.Path, handler.MediaType));
        Assert.Equal(["messaging_product", "type", "file"], handler.Parts.Select(part => part.Name));
        Assert.Equal("whatsapp", Encoding.UTF8.GetString(handler.Parts[0].Bytes));
        Assert.Equal("audio/ogg", Encoding.UTF8.GetString(handler.Parts[1].Bytes));
        Assert.Equal(("voice.ogg", "audio/ogg"), (handler.Parts[2].FileName, handler.Parts[2].ContentType));
        Assert.Equal(Audio, handler.Parts[2].Bytes);
    }

    [Theory]
    [InlineData(400, """{"error":{"code":100,"fbtrace_id":"t"}}""", 400, 100, "http_400")]
    [InlineData(500, "{}", 500, null, "http_500")]
    [InlineData(200, "{}", 200, null, "no_id")]
    [InlineData(200, """{"id":""}""", 200, null, "no_id")]
    public async Task AnUploadThatMetaDoesNotConfirmIsAFailureWithItsStatusAndCode(int status, string body, int expectedStatus, int? expectedCode, string expectedReason)
    {
        using var handler = new ScriptedHandler((HttpStatusCode)status, body);

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Null(result.Value);
        Assert.Equal(new MessagingGraphFailure(expectedStatus, expectedCode, expectedReason), result.Failure);
    }

    // Review Focus 2: message_media.meta_media_id es varchar(64); un id más largo haría fallar el commit después de enviar.
    [Fact]
    public async Task AnUploadIdLongerThanTheColumnIsAFailure()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, $$"""{"id":"{{new string('9', 65)}}"}""");

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingGraphFailure(200, null, "invalid_id"), result.Failure);
    }

    [Fact]
    public async Task AnUnreachableGraphIsAFailureWithoutStatus()
    {
        using var handler = new NetworkDownHandler();

        var result = await ClientFor(handler).UploadMediaAsync(new MessagingSender("111", "token"), Audio, "audio/ogg", TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingGraphFailure(0, null, "network"), result.Failure);
    }

    [Fact]
    public async Task AVoiceNoteGoesAsAudioWithVoiceTrueByRecipient()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"messages":[{"id":"wamid.voice"}]}""");

        var result = await ClientFor(handler).SendAudioAsync(
            new MessagingSender("111", "token"), new SendToUserId("CO.1"), "media.1", "qep:x", null, TestContext.Current.CancellationToken);

        Assert.Equal((SendOutcome.Sent, "wamid.voice"), (result.Outcome, result.Wamid));
        Assert.Equal("/v24.0/111/messages", handler.Path);
        Assert.Contains("\"type\":\"audio\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"audio\":{\"id\":\"media.1\",\"voice\":true}", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"biz_opaque_callback_data\":\"qep:x\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"recipient\":\"CO.1\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"to\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuotedVoiceNoteToALegacyPhoneCarriesToAndTheContext()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.OK, """{"messages":[{"id":"wamid.voice"}]}""");

        await ClientFor(handler).SendAudioAsync(
            new MessagingSender("111", "token"), new SendToPhone("573001234567"), "media.1", "qep:x", "wamid.quoted", TestContext.Current.CancellationToken);

        Assert.Contains("\"to\":\"573001234567\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recipient\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"context\":{\"message_id\":\"wamid.quoted\"}", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedVoiceNoteKeepsTheGraphCodeLikeText()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.BadRequest, """{"error":{"code":131053,"error_user_title":"Media upload error"}}""");

        var result = await ClientFor(handler).SendAudioAsync(
            new MessagingSender("111", "token"), new SendToUserId("CO.1"), "media.1", "qep:x", null, TestContext.Current.CancellationToken);

        Assert.Equal(new SendTextResult(SendOutcome.GraphError, null, 131053, "Media upload error"), result);
    }
```

En `MessagingTestBed.cs`, junto a `SentText` (línea 180), los records nuevos:

```csharp
internal sealed record UploadedMedia(MessagingSender Sender, byte[] Content, string MimeType);

internal sealed record SentAudio(MessagingSender Sender, SendTarget Target, string MediaId, string CallbackData, string? ContextWamid);
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~WhatsAppCloudClientTests"
```

Esperado: `error CS1061`: `WhatsAppCloudClient` no contiene `UploadMediaAsync` ni `SendAudioAsync`.

- [ ] **Step 3: Implementar**

`IWhatsAppCloudClient.cs`, en la interfaz, después de `SendTextAsync`:

```csharp
    /// <summary>Spec 2026-10-11 §6.4: <c>POST /{phone_number_id}/media</c> en multipart; devuelve el <c>id</c> del
    /// medio en Meta. Una falla lleva el status (0 = no hubo respuesta) y el <c>error.code</c> si vino.</summary>
    Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-11 §6.4: <c>type: "audio"</c> con <c>voice: true</c>, para que llegue como nota de voz.
    /// Devuelve el mismo <see cref="SendTextResult"/> que el texto (D-V4: el nombre no se cambia en este trabajo).</summary>
    Task<SendTextResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid, CancellationToken cancellationToken);
```

`WhatsAppCloudClient.cs`: agregar `using Modules.Messaging.Infrastructure.Media;` arriba y la constante debajo de `ReadReceiptTimeout`:

```csharp
    /// <summary>Spec 2026-10-11 §6.4: el nombre del archivo en la subida. Meta no lo muestra en una nota de voz.</summary>
    public const string VoiceFileName = "voice.ogg";
```

Reemplazar `SendTextAsync` entero (líneas 43-100) por esto, que comparte el armado del cuerpo y la lectura de la respuesta con el audio. El cuerpo del texto queda **igual**, campo por campo y en el mismo orden:

```csharp
    public Task<SendTextResult> SendTextAsync(
        MessagingSender sender, SendTarget target, string body, string callbackData, string? contextWamid, CancellationToken cancellationToken) =>
        PostMessageAsync(sender, MessagePayload(target, "text", new JsonObject { ["body"] = body }, callbackData, contextWamid), cancellationToken);

    public Task<SendTextResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid, CancellationToken cancellationToken) =>
        PostMessageAsync(sender, MessagePayload(target, "audio", new JsonObject { ["id"] = mediaId, ["voice"] = true }, callbackData, contextWamid), cancellationToken);

    public async Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var file = new ReadOnlyMemoryContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("whatsapp"), "messaging_product" },
            { new StringContent(mimeType), "type" },
            { file, "file", VoiceFileName },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/media") { Content = form };
        var answer = await SendAsync("upload", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(0, null, answer.UnreachableReason ?? "unreachable"));
        }

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(status, answer.Error?.Code, $"http_{status}"));
        }

        var id = ReadId(answer.Body);
        if (string.IsNullOrEmpty(id))
        {
            return new MessagingGraphResult<string>(null, new MessagingGraphFailure(status, null, "no_id"));
        }

        // Review Focus 2: meta_media_id es varchar(64). Un id que no cabe haría fallar el commit después de enviar.
        return MediaTransfer.IsValidMetaMediaId(id)
            ? new MessagingGraphResult<string>(id, null)
            : new MessagingGraphResult<string>(null, new MessagingGraphFailure(status, null, "invalid_id"));
    }

    /// <summary>Spec 2026-10-10 §6.1.4: con <c>JsonObject</c> la clave ausente no viaja (ni <c>to</c> con BSUID ni
    /// <c>recipient</c> sin él). El orden de los campos es el de siempre: el texto no cambia por compartir esto.</summary>
    private static JsonObject MessagePayload(SendTarget target, string type, JsonObject content, string callbackData, string? contextWamid)
    {
        ArgumentNullException.ThrowIfNull(target);
        var payload = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["type"] = type,
            [type] = content,
            ["biz_opaque_callback_data"] = callbackData,
        };
        switch (target)
        {
            case SendToUserId byUserId:
                payload["recipient"] = byUserId.UserId;
                break;
            case SendToPhone byPhone:
                payload["to"] = byPhone.WaId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown send target.");
        }

        if (contextWamid is not null)
        {
            payload["context"] = new JsonObject { ["message_id"] = contextWamid };
        }

        return payload;
    }

    private async Task<SendTextResult> PostMessageAsync(MessagingSender sender, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sender);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Version}/{sender.PhoneNumberId}/messages")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var answer = await SendAsync("send", request, sender.AccessToken, null, cancellationToken);
        using var response = answer.Response;
        if (response is null)
        {
            return new SendTextResult(SendOutcome.Unconfirmed, null, null, null);
        }

        // §8.3: un 5xx no confirma nada (Meta pudo haberlo aceptado); un 4xx es un rechazo con su código.
        if ((int)response.StatusCode >= 500)
        {
            return new SendTextResult(SendOutcome.Unconfirmed, null, null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SendTextResult(SendOutcome.GraphError, null, answer.Error?.Code, answer.Error?.Title);
        }

        var wamid = ReadWamid(answer.Body);
        return wamid is null
            ? new SendTextResult(SendOutcome.Unconfirmed, null, null, null)
            : new SendTextResult(SendOutcome.Sent, wamid, null, null);
    }
```

Y junto a `ReadWamid`:

```csharp
    private static string? ReadId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
```

`MessagingTestBed.cs`, dentro de `FakeWhatsAppClient`, después de `SendTextAsync`:

```csharp
    /// <summary>Spec 2026-10-11 §6.3 paso 2: lo que responde la subida a Meta.</summary>
    public MessagingGraphResult<string> NextUpload { get; set; } = new("media.default", null);

    public List<UploadedMedia> Uploads { get; } = [];

    public List<SentAudio> AudioSends { get; } = [];

    public Task<MessagingGraphResult<string>> UploadMediaAsync(
        MessagingSender sender, ReadOnlyMemory<byte> content, string mimeType, CancellationToken cancellationToken)
    {
        Uploads.Add(new UploadedMedia(sender, content.ToArray(), mimeType));
        return Task.FromResult(NextUpload);
    }

    /// <summary>Como <see cref="SendTextAsync"/>: devuelve <see cref="NextSend"/> y corre <see cref="DuringSend"/>.</summary>
    public Task<SendTextResult> SendAudioAsync(
        MessagingSender sender, SendTarget target, string mediaId, string callbackData, string? contextWamid, CancellationToken cancellationToken)
    {
        AudioSends.Add(new SentAudio(sender, target, mediaId, callbackData, contextWamid));
        DuringSend?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextSend);
    }
```

- [ ] **Step 4: Correr y verificar que pasan (las existentes del texto incluidas)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~WhatsAppCloudClientTests|FullyQualifiedName~SendMessageHandlerTests"
```

Esperado: todo verde; las 4 existentes de `WhatsAppCloudClientTests` (cuerpo del texto) y todas las de `SendMessageHandlerTests` siguen pasando.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git add src/Modules/Messaging/Modules.Messaging.Application/IWhatsAppCloudClient.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Meta/WhatsAppCloudClient.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/WhatsAppCloudClientTests.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs
git commit -m "feat(messaging): subida de medios y envío de nota de voz en el cliente de Graph"
git log -1 --format=%B
```

---

### Task 4: Persistencia del saliente con medio

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/IOutboundMessages.cs` (los dos records y la interfaz del reclamo)
- Modify: `src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs` (todo el archivo)
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs:141,155,159,164` (los cuatro commits pasan `metaMediaId: null`)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs:245-283` (`FakeClaim`)
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/OutboundAudioPersistenceTests.cs`

**Interfaces:**
- Consumes: `MessagingMediaKeys.For` (Task 1).
- Produces:
  - `public sealed record OutboundMedia(string MimeType, long SizeBytes, string Sha256Hex);`
  - `public sealed record OutboundDraft(Guid MessageId, Guid ConversationId, Guid TenantId, Guid ConnectionId, Guid ClientId, string? Text, Guid SentByMemberId, DateTimeOffset Now, Guid? ReplyToMessageId = null, string? ReplyToWamid = null, MessageKind Kind = MessageKind.Text, OutboundMedia? Media = null);`
  - `public sealed record ExistingOutbound(Guid Id, MessageStatus Status, DateTimeOffset OccurredAt, Guid? SentByMemberId, string Text, Guid? ReplyToMessageId = null, MessageKind Kind = MessageKind.Text, string? MediaMimeType = null);`
  - `IOutboundClaim.CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)`
  - `IOutboundClaim.CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)`
  - `FakeClaim.MetaMediaId` (`string?`): lo que recibió el commit.

Los parámetros nuevos de los records van al final con default: así compilan sin cambios todas las construcciones de `SendMessageHandlerTests`.

- [ ] **Step 1: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/OutboundAudioPersistenceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §6.5 y §7: el reclamo escribe <c>kind</c> desde el borrador y, si trae medio, la fila de
/// <c>message_media</c> en la misma transacción, con <c>stored_at</c> (fuera de la cola de copia). En <c>Failed</c>
/// también (D-V1), con <c>meta_media_id = ''</c> si Meta nunca dio un id (D-V2); el reintento sobrescribe la misma
/// fila. Sin migración.</summary>
public sealed class OutboundAudioPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly OutboundMedia Voice = new("audio/ogg", 1234, new string('a', 64));

    private sealed record Seeded(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, Guid ConversationId, Guid MemberId);

    private static async Task<Seeded> SeedAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        return new Seeded(factory, connectionString, tenant.TenantId, connectionId, conversationId, await OwnerMembershipIdAsync(connectionString, tenant));
    }

    private static OutboundDraft AudioDraft(Seeded seeded, Guid clientId, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), seeded.ConversationId, seeded.TenantId, seeded.ConnectionId, clientId, null, seeded.MemberId, now,
            Kind: MessageKind.Audio, Media: Voice);

    [Fact]
    public async Task ASentVoiceNoteWritesKindFourAndItsStoredMediaInTheSameTransaction()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var draft = AudioDraft(seeded, Guid.CreateVersion7(), at);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.voice", at, "media.out", Ct);
        }

        Assert.Equal("4|true|wamid.voice", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT kind || '|' || (text IS NULL) || '|' || wamid FROM messaging.messages WHERE id = @id", ("id", draft.MessageId)));
        Assert.Equal($"audio/ogg|media.out|{new string('a', 64)}|1234|messaging/{seeded.TenantId}/{draft.MessageId}|true|0", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT mime_type || '|' || meta_media_id || '|' || sha256 || '|' || size_bytes || '|' || storage_key || '|' || (stored_at IS NOT NULL) || '|' || attempts FROM messaging.message_media WHERE message_id = @id",
            ("id", draft.MessageId)));
        // La foto de la conversación, como un audio entrante (InboundIngestion.Preview): kind 4 y sin preview.
        Assert.Equal("4|true", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT last_message_kind || '|' || (last_message_preview IS NULL) FROM messaging.conversations WHERE id = @id", ("id", seeded.ConversationId)));
        // Fuera de IX_message_media_pending: el MediaCopyWorker sólo toma stored_at IS NULL.
        Assert.Equal(0L, await CountAsync(seeded.ConnectionString, "SELECT count(*) FROM messaging.message_media WHERE stored_at IS NULL"));
    }

    [Fact]
    public async Task AFailedVoiceNoteKeepsItsMediaWithoutMetaIdAndTheRetryOverwritesTheSameRow()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var clientId = Guid.CreateVersion7();
        var first = AudioDraft(seeded, clientId, at);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(first, Ct);
            await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, at, null, Ct);
        }

        Assert.Equal($"|messaging/{seeded.TenantId}/{first.MessageId}|true", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT meta_media_id || '|' || storage_key || '|' || (stored_at IS NOT NULL) FROM messaging.message_media WHERE message_id = @id", ("id", first.MessageId)));

        ExistingOutbound? existing;
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            existing = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().FindByClientIdAsync(seeded.ConversationId, clientId, Ct);
        }

        Assert.NotNull(existing);
        Assert.Equal((MessageStatus.Failed, MessageKind.Audio, "audio/ogg"), (existing.Status, existing.Kind, existing.MediaMimeType));

        var retry = AudioDraft(seeded, clientId, at.AddMinutes(1));
        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(retry, Ct);
            Assert.False(claim.Inserted);
            Assert.Equal(first.MessageId, claim.MessageId);
            Assert.Equal((MessageKind.Audio, "audio/ogg"), (claim.Existing!.Kind, claim.Existing.MediaMimeType));
            await claim.CommitSentAsync("wamid.retry", at.AddMinutes(1), "media.2", Ct);
        }

        // Una sola fila de medio, la del messageId original, con el id nuevo de Meta: no quedan huérfanos.
        Assert.Equal($"1|media.2|messaging/{seeded.TenantId}/{first.MessageId}", await ScalarAsync<string>(seeded.ConnectionString,
            "SELECT count(*) OVER () || '|' || meta_media_id || '|' || storage_key FROM messaging.message_media"));
    }

    [Fact]
    public async Task ATextMessageStaysKindOneWithoutMediaAndWithItsPreview()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedAsync(database);
        using var _ = seeded.Factory;
        var at = DateTimeOffset.UtcNow;
        var draft = new OutboundDraft(Guid.CreateVersion7(), seeded.ConversationId, seeded.TenantId, seeded.ConnectionId, Guid.CreateVersion7(), "hola", seeded.MemberId, at);

        using (var scope = seeded.Factory.Services.CreateScope())
        {
            await using var claim = await scope.ServiceProvider.GetRequiredService<IOutboundMessages>().ClaimAsync(draft, Ct);
            await claim.CommitSentAsync("wamid.text", at, null, Ct);
        }

        Assert.Equal("1|0|1|hola", await ScalarAsync<string>(seeded.ConnectionString,
            """
            SELECT m.kind || '|' || (SELECT count(*) FROM messaging.message_media mm WHERE mm.message_id = m.id) || '|' || c.last_message_kind || '|' || c.last_message_preview
            FROM messaging.messages m JOIN messaging.conversations c ON c.id = m.conversation_id WHERE m.id = @id
            """, ("id", draft.MessageId)));
    }
}
```

- [ ] **Step 2: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --filter "FullyQualifiedName~OutboundAudioPersistenceTests"
```

Esperado: `error CS0246` sobre `OutboundMedia` y `CS1739`/`CS1501` por los parámetros `Kind`/`Media` y el `metaMediaId` que todavía no existen.

- [ ] **Step 3: Implementar el contrato**

`IOutboundMessages.cs`, reemplazar los dos records de arriba y las dos firmas de commit:

```csharp
/// <summary>Spec 2026-10-11 §6.5: el medio de un saliente, ya en R2. Sin clave: la calcula el reclamo con su
/// <c>MessageId</c> (<see cref="MessagingMediaKeys.For"/>), que en un reintento de un <c>Failed</c> no es el del
/// borrador. <c>Sha256Hex</c> en minúsculas, como la copia de los entrantes.</summary>
public sealed record OutboundMedia(string MimeType, long SizeBytes, string Sha256Hex);

/// <summary>§8.3: la fila que se intenta insertar. <c>Text</c> ya viene recortado (<c>null</c> en una nota de voz);
/// <c>Now</c> es la hora del servidor al reclamar. <c>ReplyToMessageId</c>/<c>ReplyToWamid</c>: el citado ya validado
/// (spec 2026-10-10 §8.6). <c>Kind</c> y <c>Media</c>: spec 2026-10-11 §6.5; el texto sigue con <c>Text</c> y sin medio.</summary>
public sealed record OutboundDraft(
    Guid MessageId, Guid ConversationId, Guid TenantId, Guid ConnectionId, Guid ClientId, string? Text, Guid SentByMemberId,
    DateTimeOffset Now, Guid? ReplyToMessageId = null, string? ReplyToWamid = null, MessageKind Kind = MessageKind.Text, OutboundMedia? Media = null);

/// <summary>La fila que ya existía con ese <c>clientId</c> en la conversación (con <c>FOR UPDATE</c> en el reclamo).
/// <c>Kind</c> y <c>MediaMimeType</c> (spec 2026-10-11 §6.5): la idempotencia devuelve la burbuja de audio con su
/// <c>media.url</c>.</summary>
public sealed record ExistingOutbound(
    Guid Id, MessageStatus Status, DateTimeOffset OccurredAt, Guid? SentByMemberId, string Text, Guid? ReplyToMessageId = null,
    MessageKind Kind = MessageKind.Text, string? MediaMimeType = null);
```

```csharp
    /// <summary><paramref name="metaMediaId"/>: el id del medio en Meta, si el borrador trae medio (spec 2026-10-11 §6.5).</summary>
    Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken);

    /// <summary>Con medio, también escribe <c>message_media</c> (D-V1); <paramref name="metaMediaId"/> <c>null</c> si la
    /// subida a Meta falló (se guarda <c>''</c>, D-V2).</summary>
    Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken);
```

`SendMessage.cs`: en las cuatro llamadas de commit, agregar `null` antes del token (la Task 5 las mueve a `OutboundGuards`):

```csharp
                await claim.CommitSentAsync(result.Wamid, answeredAt, null, CancellationToken.None);
                await claim.CommitFailedAsync(131047, result.Title, answeredAt, null, CancellationToken.None);
                await claim.CommitFailedAsync(result.Code ?? UnknownGraphErrorCode, result.Title, answeredAt, null, CancellationToken.None);
                await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, answeredAt, null, CancellationToken.None);
```

`MessagingTestBed.cs`, en `FakeClaim`, reemplazar los dos commits y agregar la propiedad:

```csharp
    /// <summary>Lo que recibió el commit como id del medio en Meta (spec 2026-10-11 §6.5).</summary>
    public string? MetaMediaId { get; private set; }

    // Como EF/Npgsql: un token ya cancelado hace fallar el commit o el rollback antes de llegar a la base.
    public Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
    {
        MetaMediaId = metaMediaId;
        return Close("committed-sent:" + wamid, cancellationToken);
    }

    public Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
    {
        MetaMediaId = metaMediaId;
        return Close("committed-failed:" + failureCode.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }
```

- [ ] **Step 4: Implementar `OutboundMessages`**

En `OutboundMessages.cs`:

1. `ClaimAsync`: antes del `INSERT`, `var kind = MessageColumnCodes.ToCode(draft.Kind);`, y en el `VALUES` reemplazar el `2, 1, 1,` por `2, {kind}, 1,`.
2. Las dos consultas de lo existente (`ClaimAsync` con `FOR UPDATE` y `FindByClientIdAsync`) traen `kind` y el mime con un `LEFT JOIN`. El `FOR UPDATE` pasa a `FOR UPDATE OF m` (PostgreSQL no bloquea el lado nulo de un outer join):

```csharp
            var rows = await dbContext.Database.SqlQuery<ExistingRow>(
                $"""
                SELECT m.id AS "Id", m.status AS "Status", m.occurred_at AS "OccurredAt", m.sent_by_member_id AS "SentByMemberId", coalesce(m.text, '') AS "Text",
                       m.reply_to_message_id AS "ReplyToMessageId", m.kind AS "Kind", mm.mime_type AS "MediaMimeType"
                FROM messaging.messages m LEFT JOIN messaging.message_media mm ON mm.message_id = m.id
                WHERE m.conversation_id = {draft.ConversationId} AND m.client_id = {draft.ClientId} FOR UPDATE OF m
                """).ToListAsync(cancellationToken);
```

```csharp
        var rows = await dbContext.Database.SqlQuery<ExistingRow>(
            $"""
            SELECT m.id AS "Id", m.status AS "Status", m.occurred_at AS "OccurredAt", m.sent_by_member_id AS "SentByMemberId",
                   coalesce(m.text, '') AS "Text", m.reply_to_message_id AS "ReplyToMessageId", m.kind AS "Kind", mm.mime_type AS "MediaMimeType"
            FROM messaging.messages m LEFT JOIN messaging.message_media mm ON mm.message_id = m.id
            WHERE m.conversation_id = {conversationId} AND m.client_id = {clientId}
            """).ToListAsync(cancellationToken);
```

3. `ToExisting` y `ExistingRow`:

```csharp
    private static ExistingOutbound ToExisting(ExistingRow row) =>
        new(row.Id, MessageColumnCodes.ToStatus(row.Status), row.OccurredAt, row.SentByMemberId, row.Text, row.ReplyToMessageId,
            MessageColumnCodes.ToKind(row.Kind), row.MediaMimeType);
```

En la clase `ExistingRow`, agregar:

```csharp
        public short Kind { get; set; }

        public string? MediaMimeType { get; set; }
```

4. En `Claim`, reemplazar `CommitSentAsync` y `CommitFailedAsync`, y agregar los dos privados:

```csharp
        public async Task CommitSentAsync(string wamid, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
        {
            var preview = PreviewOf(draft.Text);
            var kind = MessageColumnCodes.ToCode(draft.Kind);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 1, wamid = {wamid}, occurred_at = {occurredAt}, text = {draft.Text}, kind = {kind},
                    failure_code = NULL, failure_title = NULL, sent_by_member_id = {draft.SentByMemberId},
                    reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}
                WHERE id = {MessageId}
                """, cancellationToken);
            await UpsertMediaAsync(occurredAt, metaMediaId, cancellationToken);
            // La foto de la conversación, como la ingesta de §7.5: CASE por fecha para no pisar un entrante más
            // nuevo. No sube version ni updated_at: el envío no es una edición que la persona pueda pisar, y subirla
            // convertiría un resolver justo después de responder en un 412 (mismo criterio que los acuses de §7.5).
            // Una nota de voz deja kind 4 y preview NULL, igual que un audio entrante (spec 2026-10-11 §6.5).
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.conversations SET
                    last_activity_at       = GREATEST(last_activity_at, {occurredAt}),
                    last_message_id        = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {MessageId} ELSE last_message_id END,
                    last_message_direction = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 2 ELSE last_message_direction END,
                    last_message_kind      = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {kind} ELSE last_message_kind END,
                    last_message_preview   = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN {preview} ELSE last_message_preview END,
                    last_message_status    = CASE WHEN last_message_at IS NULL OR {occurredAt} >= last_message_at THEN 1 ELSE last_message_status END,
                    last_message_at        = GREATEST(last_message_at, {occurredAt})
                WHERE id = {draft.ConversationId}
                """, cancellationToken);
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task CommitFailedAsync(int failureCode, string? failureTitle, DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
        {
            var kind = MessageColumnCodes.ToCode(draft.Kind);
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                UPDATE messaging.messages SET status = 4, failure_code = {failureCode}, failure_title = {failureTitle}, occurred_at = {occurredAt}, text = {draft.Text}, kind = {kind},
                    reply_to_message_id = {draft.ReplyToMessageId}, reply_to_wamid = {draft.ReplyToWamid}
                WHERE id = {MessageId}
                """, cancellationToken);
            // D-V1: la burbuja fallida se escucha desde otro dispositivo, y el reintento sobrescribe en vez de duplicar.
            await UpsertMediaAsync(occurredAt, metaMediaId, cancellationToken);
            // Cerrado antes del commit: si el commit falla, DisposeAsync no intenta un rollback que taparía esa falla.
            _closed = true;
            await transaction.CommitAsync(cancellationToken);
        }

        private static string? PreviewOf(string? text) =>
            text is null ? null : text.Length <= Conversation.PreviewMaxLength ? text : text[..Conversation.PreviewMaxLength];

        /// <summary>Spec 2026-10-11 §6.5 y §7: con <c>stored_at</c> no nulo la fila queda fuera de
        /// <c>IX_message_media_pending</c> y el <c>MediaCopyWorker</c> no la toma. <c>meta_media_id</c> es
        /// <c>NOT NULL</c>: sin id de Meta se guarda <c>''</c> (D-V2). La clave es la de <see cref="MessageId"/>, que en
        /// un reintento es la de la fila <c>Failed</c>: R2 sobrescribe el mismo objeto.</summary>
        private async Task UpsertMediaAsync(DateTimeOffset occurredAt, string? metaMediaId, CancellationToken cancellationToken)
        {
            if (draft.Media is not { } media)
            {
                return;
            }

            var key = MessagingMediaKeys.For(draft.TenantId, MessageId);
            var mediaId = metaMediaId ?? string.Empty;
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO messaging.message_media (message_id, mime_type, file_name, meta_media_id, sha256, size_bytes, storage_key, stored_at, attempts, next_attempt_at, last_error)
                VALUES ({MessageId}, {media.MimeType}, NULL, {mediaId}, {media.Sha256Hex}, {media.SizeBytes}, {key}, {occurredAt}, 0, {occurredAt}, NULL)
                ON CONFLICT (message_id) DO UPDATE SET
                    mime_type = EXCLUDED.mime_type, meta_media_id = EXCLUDED.meta_media_id, sha256 = EXCLUDED.sha256, size_bytes = EXCLUDED.size_bytes,
                    storage_key = EXCLUDED.storage_key, stored_at = EXCLUDED.stored_at, attempts = 0, next_attempt_at = EXCLUDED.next_attempt_at, last_error = NULL
                """, cancellationToken);
        }
```

Actualizar el `<summary>` de `IOutboundClaim` («Reenviar un Failed reescribe el texto guardado con el del request nuevo») agregando: «y, si trae medio, la fila de `message_media` (spec 2026-10-11 §6.5)».

- [ ] **Step 5: Correr y verificar que pasan (texto incluido)**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~OutboundAudioPersistenceTests|FullyQualifiedName~SendMessageApiTests"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~SendMessageHandlerTests"
```

Esperado: 3 verdes nuevas; `SendMessageApiTests` y `SendMessageHandlerTests` verdes como antes (el texto no cambió de forma).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git add src/Modules/Messaging/Modules.Messaging.Application/IOutboundMessages.cs src/Modules/Messaging/Modules.Messaging.Infrastructure/Persistence/OutboundMessages.cs src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/OutboundAudioPersistenceTests.cs
git commit -m "feat(messaging): el reclamo del saliente guarda kind y message_media en la misma transacción"
git log -1 --format=%B
```

---

### Task 5: Extraer `OutboundGuards` (D-V3) sin cambiar el envío de texto

**Files:**
- Create: `src/Modules/Messaging/Modules.Messaging.Application/OutboundGuards.cs`
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs:31-202` (handler)
- **No se toca:** `tests/Modules/Messaging/Modules.Messaging.UnitTests/SendMessageHandlerTests.cs` ni `MessagingTestBed.SendHandler()`.

**Interfaces:**
- Consumes: `IOutboundClaim` con `metaMediaId` (Task 4).
- Produces (todo `internal`, mismo assembly):
  - `internal sealed record OutboundTarget(ConversationRow Conversation, Guid Member);`
  - `internal sealed record OutboundPermit(DateTimeOffset Now, ReplyTargetRow? Quoted);`
  - `internal sealed class OutboundGuards(IConversationQueries, IConversationRepository, IMessageQueries, IOutboundMessages, IMessagingConnectionDirectory, IMessagingMemberNames, IMembershipDirectory, ITenantModules, IExecutionContext, IClock)` con:
    - `DateTimeOffset UtcNow { get; }`
    - `Task<OutboundTarget> LoadAsync(Guid tenantId, Guid conversationId, CancellationToken)`
    - `Task<ExistingOutbound?> FindAlreadySentAsync(Guid conversationId, Guid clientId, CancellationToken)`
    - `Task<OutboundPermit> AdmitAsync(Guid tenantId, OutboundTarget target, Guid? replyTo, CancellationToken)`
    - `Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken)`
    - `static Task<ExistingOutbound?> TakeInFlightAsync(IOutboundClaim claim)`
    - `Task<MessagingSender> ResolveSenderAsync(Guid tenantId, Guid connectionId, IOutboundClaim claim, CancellationToken)`
    - `Task SettleAsync(Guid tenantId, Guid connectionId, IOutboundClaim claim, SendTextResult result, DateTimeOffset answeredAt, string? metaMediaId, Func<Task>? afterRollback)` — vuelve sólo si quedó `Sent`; si no, lanza.
    - `Task<MessageDto> ToDtoAsync(Guid tenantId, MessageRow row, CancellationToken)`
    - `static MessageRow ExistingRow(Guid conversationId, ExistingOutbound existing, Guid? clientId)`

Es un refactor: **no hay RED**. El candado son las pruebas existentes de `SendMessageHandlerTests`, que tienen que dar el mismo número de verdes antes y después, sin tocar el archivo.

- [ ] **Step 1: Baseline verde**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~SendMessageHandlerTests"
```

Anotar en el handoff el total literal (`Correctas: N`).

- [ ] **Step 2: Crear `OutboundGuards.cs`**

```csharp
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §8.5, pasos 2 a 4: la conversación del tenant y la membresía activa de quien envía.</summary>
internal sealed record OutboundTarget(ConversationRow Conversation, Guid Member);

/// <summary>Lo que dejan pasar los pasos 6 a 9: la hora del servidor al admitir y el citado ya validado (o <c>null</c>).</summary>
internal sealed record OutboundPermit(DateTimeOffset Now, ReplyTargetRow? Quoted);

/// <summary>
/// Spec 2026-10-11 §6.3 (D-V3): las guardas que comparten el envío de texto y el de nota de voz, en el orden del spec
/// 2026-10-10 §8.5, y la tabla de respuestas de Meta del spec 2026-10-09 §8.3. Cada handler pone lo suyo entre el
/// reclamo y la tabla: el texto llama a Meta una vez; la nota de voz sube a R2, sube a Meta y envía.
/// No se registra en DI: cada handler la arma con sus propias dependencias, y por eso las pruebas de
/// <see cref="SendMessageHandler"/> no cambiaron al extraerla.
/// </summary>
internal sealed class OutboundGuards(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
{
    public DateTimeOffset UtcNow => clock.UtcNow;

    /// <summary>Pasos 2 a 4: tenant, permiso y módulo → conversación del tenant → membresía activa.</summary>
    public async Task<OutboundTarget> LoadAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, tenantId, MessagingPermissions.ConversationManage, cancellationToken);
        var conversation = await conversations.FindAsync(tenantId, conversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(conversationId);
        var member = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken)
            ?? throw new RequestForbiddenException("authorization.denied", "The subject does not have an active membership in this tenant.");
        return new OutboundTarget(conversation, member);
    }

    /// <summary>Paso 5: lo que ya salió, ya salió, antes de mirar estado, ventana o dueño (sin candado). Un
    /// <c>Failed</c> no cuenta: se reenvía.</summary>
    public async Task<ExistingOutbound?> FindAlreadySentAsync(Guid conversationId, Guid clientId, CancellationToken cancellationToken) =>
        await outbound.FindByClientIdAsync(conversationId, clientId, cancellationToken) is { Status: not MessageStatus.Failed } sent ? sent : null;

    /// <summary>Pasos 6 a 9: status Open → ventana → el citado → dueño. Todo lo que puede fallar por validación va antes
    /// de la autoasignación: un request inválido no cambia estado.</summary>
    public async Task<OutboundPermit> AdmitAsync(Guid tenantId, OutboundTarget target, Guid? replyTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var conversation = target.Conversation;
        var now = clock.UtcNow;
        if (conversation.Status != ConversationStatus.Open)
        {
            throw new MessagingDomainException(MessagingErrorCodes.ConversationNotOpen, "The conversation is resolved; reopen it to answer.");
        }

        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "More than 24 hours passed since the person last wrote.");
        }

        // §8.5: lo citado es de esta conversación, salió por WhatsApp y no es una reacción ni un evento. Va antes del
        // dueño: un request que no pasa la validación no cambia estado, así que una cita inválida no autoasigna.
        // Nunca se manda a Meta un context.message_id de otra conversación.
        ReplyTargetRow? quoted = null;
        if (replyTo is { } quotedId)
        {
            var targets = await messages.FindReplyTargetsAsync(tenantId, [quotedId], cancellationToken);
            if (!targets.TryGetValue(quotedId, out quoted) || quoted.ConversationId != conversation.Id || quoted.Wamid is null
                || quoted.Kind is MessageKind.Reaction or MessageKind.Event)
            {
                throw new ValidationException([new ValidationFailure("replyTo", "Elige un mensaje de esta conversación que se pueda citar.")]);
            }
        }

        // §8.5: sólo el asignado responde; sin asignar, responder es tomar (atómico, fuera del candado largo).
        // Si Meta rechaza después, la conversación queda asignada: intentar responder es tomar.
        if (conversation.AssignedMemberId is { } owner
                ? owner != target.Member
                : await repository.TryAutoAssignAsync(tenantId, conversation.Id, target.Member, executionContext.SubjectId, now, cancellationToken) == AutoAssignOutcome.AssignedToOther)
        {
            throw new MessagingDomainException(MessagingErrorCodes.AssignedToOther, "The conversation is assigned to someone else.");
        }

        return new OutboundPermit(now, quoted);
    }

    public Task<IOutboundClaim> ClaimAsync(OutboundDraft draft, CancellationToken cancellationToken) => outbound.ClaimAsync(draft, cancellationToken);

    /// <summary>Idempotencia entre dos requests en vuelo con el mismo clientId: el segundo esperó el candado del primero.
    /// Devuelve lo existente (ya con el reclamo cerrado) o <c>null</c> si hay que seguir.</summary>
    public static async Task<ExistingOutbound?> TakeInFlightAsync(IOutboundClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!claim.Inserted && claim.Existing is { } existing && existing.Status != MessageStatus.Failed)
        {
            await claim.RollbackAsync(CancellationToken.None);
            return existing;
        }

        return null;
    }

    /// <summary>Paso 11: la conexión <c>Active</c>, ya con el reclamo tomado.</summary>
    public async Task<MessagingSender> ResolveSenderAsync(Guid tenantId, Guid connectionId, IOutboundClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var sender = await connections.ResolveSenderAsync(tenantId, connectionId, cancellationToken);
        if (sender is null)
        {
            await claim.RollbackAsync(CancellationToken.None);
            throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        }

        return sender;
    }

    /// <summary>
    /// §8.3, la tabla de respuestas. Vuelve sólo si quedó <c>Sent</c>; en cualquier otro caso cierra el reclamo y lanza.
    /// Todo cierre va con <c>CancellationToken.None</c>: si Meta ya aceptó, un rollback por cancelación borraría la fila
    /// y el reintento con el mismo clientId lo mandaría dos veces. <paramref name="afterRollback"/> corre después de un
    /// rollback por credencial (la nota de voz borra ahí lo que subió a R2).
    /// </summary>
    public async Task SettleAsync(
        Guid tenantId, Guid connectionId, IOutboundClaim claim, SendTextResult result, DateTimeOffset answeredAt, string? metaMediaId, Func<Task>? afterRollback)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case SendOutcome.Sent when !string.IsNullOrEmpty(result.Wamid):
                await claim.CommitSentAsync(result.Wamid, answeredAt, metaMediaId, CancellationToken.None);
                return;

            case SendOutcome.GraphError when result.Code is 190 or 133010:
                // §8.3: la credencial o el número ya no sirven; la fila no se guarda y la conexión pasa a NeedsAttention.
                await claim.RollbackAsync(CancellationToken.None);
                if (afterRollback is not null)
                {
                    await afterRollback();
                }

                await ReportRejectedAsync(tenantId, connectionId, result.Code == 190 ? "token_expired" : "number_unregistered");
                throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials.");

            case SendOutcome.GraphError when result.Code is 131047:
                await claim.CommitFailedAsync(131047, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.WindowClosed, "Meta closed the 24-hour window.");

            case SendOutcome.GraphError:
                await claim.CommitFailedAsync(result.Code ?? SendMessageHandler.UnknownGraphErrorCode, result.Title, answeredAt, metaMediaId, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "Meta rejected the message.");

            default:
                // Timeout, 5xx, red o un 2xx sin messages[0].id: Meta pudo haberlo aceptado (riesgo residual de §8.3).
                await claim.CommitFailedAsync(MessageFailureReasons.Unconfirmed, null, answeredAt, metaMediaId, CancellationToken.None);
                throw new MessagingDomainException(MessagingErrorCodes.MessageRejected, "The send could not be confirmed with Meta.");
        }
    }

    // El mensaje sale con el mapeo de la lista (MessageMapping): mismo sentBy.displayName, nunca null (D-M20).
    // En la idempotencia se devuelve lo guardado, no lo del request.
    public async Task<MessageDto> ToDtoAsync(Guid tenantId, MessageRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var names = row.SentByMemberId is { } member
            ? await memberNames.FindAsync(tenantId, [member], cancellationToken)
            : new Dictionary<Guid, string>();
        // §5.1: repetir un clientId devuelve el replyTo guardado, aunque el cuerpo traiga otro.
        var targets = row.ReplyToMessageId is { } quoted
            ? await messages.FindReplyTargetsAsync(tenantId, [quoted], cancellationToken)
            : new Dictionary<Guid, ReplyTargetRow>();
        return MessageMapping.ToDto(row, tenantId, names, targets);
    }

    /// <summary>Spec 2026-10-11 §6.5: una nota de voz guardada vuelve como audio, sin texto y con su medio.</summary>
    public static MessageRow ExistingRow(Guid conversationId, ExistingOutbound existing, Guid? clientId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return new MessageRow(
            existing.Id, conversationId, MessageDirection.Outbound, existing.Kind, existing.Kind == MessageKind.Text ? existing.Text : null, null, null,
            existing.Status, null, existing.OccurredAt, existing.SentByMemberId, clientId,
            existing.MediaMimeType is { } mimeType ? new MessageMediaRow(mimeType, null) : null, existing.ReplyToMessageId);
    }

    // Si Integrations no logra pasar la conexión a NeedsAttention, la respuesta sigue siendo el 422 documentado;
    // la causa viaja como InnerException y ApiExceptionHandler la deja en platform.request_failures (Application no
    // tiene logger). El próximo envío vuelve a recibir 190/133010 y reintenta el reporte.
    private async Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode)
    {
        try
        {
            await connections.ReportRejectedAsync(tenantId, connectionId, failureCode, CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.ConnectionUnavailable, "Meta rejected the connection credentials; the connection state could not be updated.", exception);
        }
    }
}
```

- [ ] **Step 3: `SendMessageHandler` pasa a usarla**

En `SendMessage.cs`, el constructor primario **no cambia** (mismos 12 parámetros, mismo orden: `MessagingTestBed.SendHandler()` y DI no se tocan). Reemplazar el cuerpo de la clase, desde `public const string CallbackPrefix` hasta el final, por:

```csharp
{
    public const string CallbackPrefix = "qep:";

    /// <summary>§8.3: un 4xx de Graph sin <c>error.code</c> legible se guarda con este <c>failure_code</c>; no está en
    /// la tabla de §10.3, así que <c>MessageFailureReasons</c> lo muestra con el texto genérico.</summary>
    public const int UnknownGraphErrorCode = 0;

    // CS9124: los parámetros que van a la guarda no se usan en los métodos; el reloj y el reclamo se piden a ella.
    private readonly OutboundGuards _guards = new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock);

    public async Task<MessageDto> HandleAsync(SendMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;

        // Spec 2026-10-10 §8.5 paso 2: lo que ya salió, ya salió, antes de mirar estado, ventana o dueño.
        if (await _guards.FindAlreadySentAsync(conversation.Id, command.ClientId!.Value, cancellationToken) is { } sent)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, sent, command.ClientId), cancellationToken);
        }

        var permit = await _guards.AdmitAsync(command.TenantId, target, command.ReplyTo, cancellationToken);
        var text = command.Text!.Trim();
        var draft = new OutboundDraft(Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, text, target.Member, permit.Now,
            permit.Quoted?.Id, permit.Quoted?.Wamid);
        await using var claim = await _guards.ClaimAsync(draft, cancellationToken);

        // Desde acá, todo cierre del reclamo va con CancellationToken.None: si la persona cierra la pestaña
        // (RequestAborted) mientras Meta ya aceptó el mensaje, un rollback por cancelación borraría la fila y el
        // reintento con el mismo clientId lo mandaría dos veces. La llamada a Meta igual tiene tope: los 10 s
        // del cliente messaging.meta-graph.
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, inFlight, command.ClientId), cancellationToken);
        }

        var sender = await _guards.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, claim, cancellationToken);
        var result = await meta.SendTextAsync(
            sender, SendTarget.For(conversation.UserId, conversation.WaId), text, CallbackPrefix + claim.MessageId.ToString("D"), permit.Quoted?.Wamid, CancellationToken.None);
        // §8.3: occurred_at del saliente = hora del servidor al recibir la respuesta de Meta.
        var answeredAt = _guards.UtcNow;
        await _guards.SettleAsync(command.TenantId, conversation.ConnectionId, claim, result, answeredAt, metaMediaId: null, afterRollback: null);
        return await _guards.ToDtoAsync(
            command.TenantId,
            new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, MessageKind.Text, text, null, null, MessageStatus.Sent, null, answeredAt, target.Member,
                command.ClientId, null, ReplyToMessageId: permit.Quoted?.Id),
            CancellationToken.None);
    }
}
```

Se borran del archivo `ReportRejectedAsync`, `ToDtoAsync` y `ExistingRow` (viven en la guarda). En los `using` de `SendMessage.cs` se quita `using FluentValidation.Results;` (el `ValidationFailure` de la cita se fue con `AdmitAsync`); `BuildingBlocks.Application`, `FluentValidation`, `Modules.Messaging.Domain` y `Modules.Tenancy.Application` se quedan (validador, constructor y `MessageRow`). Actualizar el `<summary>` del handler con una línea al final: «Las guardas, el reclamo y la tabla viven en `OutboundGuards` (spec 2026-10-11, D-V3).»

- [ ] **Step 4: Verde igual que el baseline, sin tocar las pruebas**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~SendMessageHandlerTests|FullyQualifiedName~SendAudioMessageValidatorTests|FullyQualifiedName~WhatsAppCloudClientTests"
git -C "$B" diff --stat -- tests/Modules/Messaging/Modules.Messaging.UnitTests/SendMessageHandlerTests.cs
```

Esperado: `SendMessageHandlerTests` con el **mismo** `Correctas: N` del Step 1 y cero fallas; el `git diff --stat` no imprime nada.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git diff --quiet -- tests/Modules/Messaging/Modules.Messaging.UnitTests/SendMessageHandlerTests.cs || exit 1
git add src/Modules/Messaging/Modules.Messaging.Application/OutboundGuards.cs src/Modules/Messaging/Modules.Messaging.Application/SendMessage.cs
git commit -m "refactor(messaging): extraer las guardas del envío a OutboundGuards"
git log -1 --format=%B
```

---

### Task 6: `SendAudioMessageHandler`

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Application/SendAudioMessage.cs` (agregar el handler)
- Modify: `tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs` (`InMemoryMediaStore`, `Store`, `SendAudioHandler()`)
- Create: `tests/Modules/Messaging/Modules.Messaging.UnitTests/SendAudioMessageHandlerTests.cs`

**Interfaces:**
- Consumes: `OutboundGuards` (Task 5), `UploadMediaAsync`/`SendAudioAsync` (Task 3), `OutboundMedia`/`metaMediaId` (Task 4), `MessagingMediaKeys.For`, `OggOpusSniffer.MimeType`, `MessagingErrorCodes.MediaUploadFailed` (Task 1), `SendAudioMessageValidator` (Task 2).
- Produces:
  - `public sealed class SendAudioMessageHandler(IConversationQueries conversations, IConversationRepository repository, IMessageQueries messages, IOutboundMessages outbound, IWhatsAppCloudClient meta, IMessagingMediaStore store, IMessagingConnectionDirectory connections, IMessagingMemberNames memberNames, IMembershipDirectory membershipDirectory, ITenantModules tenantModules, IExecutionContext executionContext, IClock clock, IValidator<SendAudioMessageCommand> validator) : ICommandHandler<SendAudioMessageCommand, MessageDto>`
  - En el banco: `InMemoryMediaStore Store`, `SendAudioMessageHandler SendAudioHandler(Guid? tenantId = null)`.

- [ ] **Step 1: Dobles del banco de pruebas**

En `MessagingTestBed.cs`, en la clase `MessagingTestBed`, debajo de `Messages`:

```csharp
    public InMemoryMediaStore Store { get; } = new();
```

y debajo de `SendHandler`:

```csharp
    public SendAudioMessageHandler SendAudioHandler(Guid? tenantId = null)
    {
        var membership = new FakeMembershipDirectory();
        membership.Active[(SubjectId, TenantId)] = MemberId;
        return new SendAudioMessageHandler(
            Conversations,
            Repository,
            Messages,
            Outbound,
            Meta,
            Store,
            Connections,
            new FakeMemberNames(MemberId),
            membership,
            TenantModules,
            new FakeExecutionContext(tenantId ?? TenantId, SubjectId, MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage),
            new FakeClock(Now),
            new SendAudioMessageValidator());
    }
```

Al final del archivo:

```csharp
/// <summary>Spec 2026-10-11 §6.3: R2 en un diccionario. <see cref="UploadFailure"/> simula que R2 no aceptó el audio.</summary>
internal sealed class InMemoryMediaStore : IMessagingMediaStore
{
    public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

    public List<string> Deleted { get; } = [];

    public Exception? UploadFailure { get; set; }

    public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
    {
        if (UploadFailure is { } failure)
        {
            throw failure;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Objects[key] = (buffer.ToArray(), contentType);
    }

    public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Deleted.Add(key);
        Objects.Remove(key);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.UnitTests/SendAudioMessageHandlerTests.cs`:

```csharp
using System.Security.Cryptography;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.3 y §10: mismas guardas y mismo orden que el texto; dentro del reclamo R2 → subida a
/// Meta → envío → la tabla del texto. D-V1: el Failed guarda el medio. Review Focus 1: un rollback sólo borra de R2 lo
/// que subió una fila nueva.</summary>
public sealed class SendAudioMessageHandlerTests
{
    private static readonly byte[] Voice = OggOpusFixture.VoiceNote();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SendAudioMessageCommand Command(MessagingTestBed bed, Guid conversationId, Guid? replyTo = null, byte[]? audio = null) =>
        new(bed.TenantId, conversationId, bed.ClientId, audio ?? Voice, replyTo);

    [Fact]
    public async Task AHappyVoiceNoteIsStoredUploadedSentAsVoiceAndCommittedWithTheMediaId()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: bed.MemberId);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.out", null);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.voice", null, null);

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal(("Audio", "Sent", (string?)null, bed.ClientId), (message.Kind, message.Status, message.Text, message.ClientId));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{message.Id}", "audio/ogg", null, null), message.Media);
        var stored = Assert.Single(bed.Store.Objects);
        Assert.Equal(($"messaging/{bed.TenantId}/{message.Id}", "audio/ogg"), (stored.Key, stored.Value.ContentType));
        Assert.Equal(Voice, stored.Value.Bytes);
        var upload = Assert.Single(bed.Meta.Uploads);
        Assert.Equal("audio/ogg", upload.MimeType);
        Assert.Equal(Voice, upload.Content);
        var send = Assert.Single(bed.Meta.AudioSends);
        Assert.Equal(("media.out", $"qep:{message.Id}", (string?)null), (send.MediaId, send.CallbackData, send.ContextWamid));
        Assert.Equal((SendTarget)new SendToPhone(conversation.WaId!), send.Target);
        Assert.Empty(bed.Meta.Sends);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal(("committed-sent:wamid.voice", "media.out"), (claim.Outcome, claim.MetaMediaId));
        Assert.Equal((MessageKind.Audio, (string?)null), (claim.Draft.Kind, claim.Draft.Text));
        Assert.Equal(new OutboundMedia("audio/ogg", Voice.Length, Convert.ToHexStringLower(SHA256.HashData(Voice))), claim.Draft.Media);
    }

    [Theory]
    [InlineData("resolved", MessagingErrorCodes.ConversationNotOpen)]
    [InlineData("window-closed", MessagingErrorCodes.WindowClosed)]
    [InlineData("never-wrote", MessagingErrorCodes.WindowClosed)]
    [InlineData("assigned-to-other", MessagingErrorCodes.AssignedToOther)]
    [InlineData("connection-paused", MessagingErrorCodes.ConnectionUnavailable)]
    public async Task TheChecksHaveTheSameCodesAndOrderAsText(string scenario, string expected)
    {
        var bed = new MessagingTestBed();
        var conversation = scenario switch
        {
            "resolved" => bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true),
            "window-closed" => bed.OpenConversation(lastInboundHoursAgo: 25),
            "never-wrote" => bed.OpenConversation(lastInboundHoursAgo: null),
            "assigned-to-other" => bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7()),
            _ => bed.OpenConversation(lastInboundHoursAgo: 1),
        };
        if (scenario == "connection-paused")
        {
            bed.Connections.Sender = null;
        }

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(expected, error.Code);
        // Nada sube a R2 ni a Meta antes de pasar todas las guardas; la conexión se mira ya con el reclamo tomado.
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.Uploads);
        Assert.Empty(bed.Meta.AudioSends);
        Assert.Equal(scenario == "connection-paused" ? ["rolled-back"] : [], bed.Outbound.Claims.Select(claim => claim.Outcome));
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenAMissingConversationIsNotFoundAndAWebmIsAValidationError()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SendAudioHandler(tenantId: Guid.CreateVersion7()).HandleAsync(Command(bed, conversation.Id), Ct));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.SendAudioHandler().HandleAsync(Command(bed, Guid.CreateVersion7()), Ct));
        var webm = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id, audio: OggOpusFixture.WebmHeader()), Ct));

        Assert.Equal(MessagingErrorCodes.ConversationNotFound, missing.Code);
        Assert.Contains(webm.Errors, failure => failure.PropertyName == "file");
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Store.Objects);
    }

    [Fact]
    public async Task AnR2FailureIs422UploadFailedWithoutRowNorMeta()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Store.UploadFailure = new IOException("R2 no responde");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(MessagingErrorCodes.MediaUploadFailed, error.Code);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Meta.Uploads);
        Assert.Empty(bed.Meta.AudioSends);
    }

    [Theory]
    [InlineData(400, 100, "committed-failed:100", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(500, null, "committed-failed:-1", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(0, null, "committed-failed:-1", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(200, null, "committed-failed:-1", MessagingErrorCodes.MessageRejected, null)]
    [InlineData(401, 190, "rolled-back", MessagingErrorCodes.ConnectionUnavailable, "token_expired")]
    [InlineData(400, 133010, "rolled-back", MessagingErrorCodes.ConnectionUnavailable, "number_unregistered")]
    public async Task AFailedUploadToMetaFollowsTheSendTableWithoutSending(int httpStatus, int? graphCode, string expectedClaim, string expectedCode, string? reported)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(httpStatus, graphCode, "x"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(expectedCode, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((expectedClaim, (string?)null), (claim.Outcome, claim.MetaMediaId));
        Assert.Empty(bed.Meta.AudioSends);
        Assert.Equal(reported is null ? [] : [reported], bed.Connections.Reported.Select(report => report.FailureCode));
        // Rollback de una fila nueva: el audio no tiene quién lo sirva y se borra. Failed: se queda (D-V1).
        var key = $"messaging/{bed.TenantId}/{claim.MessageId}";
        Assert.Equal(reported is null ? [] : [key], bed.Store.Deleted);
    }

    [Theory]
    [InlineData(131026, "committed-failed:131026", MessagingErrorCodes.MessageRejected)]
    [InlineData(131047, "committed-failed:131047", MessagingErrorCodes.WindowClosed)]
    public async Task ASendRejectedAfterTheUploadKeepsTheMediaIdAndTheAudio(int graphCode, string expectedClaim, string expectedCode)
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextUpload = new MessagingGraphResult<string>("media.out", null);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, graphCode, "title");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(expectedCode, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal((expectedClaim, "media.out"), (claim.Outcome, claim.MetaMediaId));
        Assert.Single(bed.Store.Objects);
        Assert.Empty(bed.Store.Deleted);
    }

    [Fact]
    public async Task ARejectedCredentialOnTheSendRollsBackAndDeletesTheStoredAudio()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.GraphError, null, 190, "title");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("rolled-back", claim.Outcome);
        Assert.Equal([$"messaging/{bed.TenantId}/{claim.MessageId}"], bed.Store.Deleted);
        Assert.Empty(bed.Store.Objects);
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredVoiceNoteWithoutUploading()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var stored = Guid.CreateVersion7();
        bed.Outbound.Committed = new ExistingOutbound(stored, MessageStatus.Delivered, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal((stored, "Audio", "Delivered", (string?)null), (message.Id, message.Kind, message.Status, message.Text));
        Assert.Equal(new MediaDto($"/api/v1/tenants/{bed.TenantId}/messaging/media/{stored}", "audio/ogg", null, null), message.Media);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.Uploads);
    }

    [Fact]
    public async Task AnInFlightClientIdIsResolvedByTheClaimWithoutUploading()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Outbound.Existing = new ExistingOutbound(Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-1), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal("Audio", message.Kind);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Store.Objects);
        Assert.Empty(bed.Meta.Uploads);
    }

    [Fact]
    public async Task ARetryOfAFailedVoiceNoteReusesTheMessageIdAndTheR2Key()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var failed = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(failed, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct);

        Assert.Equal((failed, "Sent"), (message.Id, message.Status));
        Assert.Equal($"messaging/{bed.TenantId}/{failed}", Assert.Single(bed.Store.Objects).Key);
        Assert.Equal($"qep:{failed}", Assert.Single(bed.Meta.AudioSends).CallbackData);
    }

    // Review Focus 1: el objeto de R2 es el que la fila Failed ya sirve; borrarlo dejaría la burbuja muda.
    [Fact]
    public async Task ARejectedCredentialOnARetryKeepsTheAudioTheFailedRowServes()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var failed = Guid.CreateVersion7();
        bed.Outbound.Existing = new ExistingOutbound(failed, MessageStatus.Failed, bed.Now.AddMinutes(-5), bed.MemberId, "", null, MessageKind.Audio, "audio/ogg");
        bed.Meta.NextUpload = new MessagingGraphResult<string>(null, new MessagingGraphFailure(401, 190, "x"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Store.Deleted);
        Assert.Equal($"messaging/{bed.TenantId}/{failed}", Assert.Single(bed.Store.Objects).Key);
    }

    [Fact]
    public async Task AQuotedVoiceNoteOnAnUnassignedConversationTakesItAndCarriesTheContext()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var quoted = Guid.CreateVersion7();
        bed.Messages.Targets[quoted] = new ReplyTargetRow(quoted, conversation.Id, MessageDirection.Inbound, MessageKind.Text, "¿Tienen?", null, "wamid.q");

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id, replyTo: quoted), Ct);

        Assert.Equal("wamid.q", Assert.Single(bed.Meta.AudioSends).ContextWamid);
        Assert.Equal(quoted, message.ReplyTo!.Id);
        Assert.Equal([conversation.Id], bed.Repository.AutoAssigned);
        Assert.Equal((quoted, "wamid.q"), (bed.Outbound.Claims[0].Draft.ReplyToMessageId!.Value, bed.Outbound.Claims[0].Draft.ReplyToWamid));
    }

    // Como el texto: la persona cierra la pestaña mientras Meta acepta la nota. Ya salió: la fila queda Sent.
    [Fact]
    public async Task AnAbortedRequestAfterMetaAcceptedStillCommitsSent()
    {
        var bed = new MessagingTestBed();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        using var aborted = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bed.Meta.NextSend = new SendTextResult(SendOutcome.Sent, "wamid.aborted", null, null);
        bed.Meta.DuringSend = aborted.Cancel;

        var message = await bed.SendAudioHandler().HandleAsync(Command(bed, conversation.Id), aborted.Token);

        Assert.Equal("Sent", message.Status);
        Assert.Equal("committed-sent:wamid.aborted", Assert.Single(bed.Outbound.Claims).Outcome);
    }
}
```

- [ ] **Step 3: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --filter "FullyQualifiedName~SendAudioMessageHandlerTests"
```

Esperado: `error CS0246` sobre `SendAudioMessageHandler`.

- [ ] **Step 4: Implementar el handler**

En `SendAudioMessage.cs`, agregar a los `using`:

```csharp
using System.Security.Cryptography;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
```

y al final del archivo:

```csharp
/// <summary>
/// Spec 2026-10-11 §6.3: mismo orden que <see cref="SendMessageHandler"/> (spec 2026-10-10 §8.5), con las mismas
/// guardas (<see cref="OutboundGuards"/>). Dentro del reclamo, en este orden: R2 → subida a Meta → envío con
/// <c>voice: true</c> → la tabla del texto. El candado de la fila dura las tres llamadas (≤ 10 s cada una en
/// <c>messaging.meta-graph</c>, R2 con su propio tope) y sigue bloqueando una sola fila de mensaje, nunca la
/// conversación.
/// </summary>
public sealed class SendAudioMessageHandler(
    IConversationQueries conversations,
    IConversationRepository repository,
    IMessageQueries messages,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingMediaStore store,
    IMessagingConnectionDirectory connections,
    IMessagingMemberNames memberNames,
    IMembershipDirectory membershipDirectory,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SendAudioMessageCommand> validator)
    : ICommandHandler<SendAudioMessageCommand, MessageDto>
{
    // CS9124: los parámetros que van a la guarda no se usan en los métodos; el reloj y el reclamo se piden a ella.
    private readonly OutboundGuards _guards = new(
        conversations, repository, messages, outbound, connections, memberNames, membershipDirectory, tenantModules, executionContext, clock);

    public async Task<MessageDto> HandleAsync(SendAudioMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var target = await _guards.LoadAsync(command.TenantId, command.ConversationId, cancellationToken);
        var conversation = target.Conversation;

        // §4.1: repetir un clientId que no está Failed devuelve la nota guardada, aunque el archivo del request sea otro.
        if (await _guards.FindAlreadySentAsync(conversation.Id, command.ClientId!.Value, cancellationToken) is { } sent)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, sent, command.ClientId), cancellationToken);
        }

        var permit = await _guards.AdmitAsync(command.TenantId, target, command.ReplyTo, cancellationToken);
        // El validador ya garantizó el audio: presente, de 1 B a 16 MB y OGG/Opus.
        var audio = command.Audio!;
        var media = new OutboundMedia(OggOpusSniffer.MimeType, audio.Length, Convert.ToHexStringLower(SHA256.HashData(audio)));
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, command.TenantId, conversation.ConnectionId, command.ClientId!.Value, Text: null, target.Member, permit.Now,
            permit.Quoted?.Id, permit.Quoted?.Wamid, MessageKind.Audio, media);
        await using var claim = await _guards.ClaimAsync(draft, cancellationToken);

        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return await _guards.ToDtoAsync(command.TenantId, OutboundGuards.ExistingRow(conversation.Id, inFlight, command.ClientId), cancellationToken);
        }

        var sender = await _guards.ResolveSenderAsync(command.TenantId, conversation.ConnectionId, claim, cancellationToken);

        // 1. R2, con la clave del reclamo: un reintento de un Failed reutiliza el messageId y sobrescribe el mismo objeto.
        var key = MessagingMediaKeys.For(command.TenantId, claim.MessageId);
        try
        {
            using var content = new MemoryStream(audio, writable: false);
            await store.UploadAsync(key, content, audio.Length, OggOpusSniffer.MimeType, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No se llamó a Meta y la fila no queda: reintentar con el mismo clientId es seguro.
            await claim.RollbackAsync(CancellationToken.None);
            throw new MessagingDomainException(MessagingErrorCodes.MediaUploadFailed, "The voice note could not be stored.", exception);
        }

        // 2 y 3. Desde acá, como el texto, todo va con CancellationToken.None: si Meta ya aceptó, cortar por la pestaña
        // cerrada borraría la fila y el reintento mandaría la nota dos veces. Una subida fallida no envió nada todavía, así
        // que entra a la misma tabla sin riesgo de duplicar.
        var upload = await meta.UploadMediaAsync(sender, audio, OggOpusSniffer.MimeType, CancellationToken.None);
        var mediaId = upload is { Succeeded: true, Value: { Length: > 0 } uploaded } ? uploaded : null;
        var result = mediaId is null
            ? FromUploadFailure(upload.Failure)
            : await meta.SendAudioAsync(
                sender, SendTarget.For(conversation.UserId, conversation.WaId), mediaId, SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"),
                permit.Quoted?.Wamid, CancellationToken.None);
        var answeredAt = _guards.UtcNow;

        // 4. La tabla del texto. Un rollback por credencial deja sin fila lo que subió una fila nueva: se borra de R2. En
        // un reintento de un Failed el objeto es el que esa fila ya sirve, y se queda.
        await _guards.SettleAsync(
            command.TenantId, conversation.ConnectionId, claim, result, answeredAt, mediaId, claim.Inserted ? () => DiscardStoredAsync(key) : null);
        return await _guards.ToDtoAsync(
            command.TenantId,
            new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, MessageKind.Audio, null, null, null, MessageStatus.Sent, null, answeredAt, target.Member,
                command.ClientId, new MessageMediaRow(OggOpusSniffer.MimeType, null), permit.Quoted?.Id),
            CancellationToken.None);
    }

    /// <summary>§6.3 paso 2: una subida fallida usa la tabla del envío. Un 4xx es un rechazo con su código (190/133010
    /// pasan la conexión a NeedsAttention); 5xx, timeout, red o un 2xx sin <c>id</c> no confirman nada.</summary>
    private static SendTextResult FromUploadFailure(MessagingGraphFailure? failure) =>
        failure is { HttpStatus: >= 400 and < 500 }
            ? new SendTextResult(SendOutcome.GraphError, null, failure.Code, null)
            : new SendTextResult(SendOutcome.Unconfirmed, null, null, null);

    // Best effort: si borrar falla queda un objeto sin fila en el bucket privado, que nadie puede pedir (el GET de media
    // va por la fila). No vale cambiar la respuesta por eso.
    private async Task DiscardStoredAsync(string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ver comentario.
        }
    }
}
```

- [ ] **Step 5: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\Modules.Messaging.UnitTests.csproj" --no-build --filter "FullyQualifiedName~SendAudioMessageHandlerTests|FullyQualifiedName~SendMessageHandlerTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests"
```

Esperado: todo verde; `SendMessageHandlerTests` con el mismo total de la Task 5.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git add src/Modules/Messaging/Modules.Messaging.Application/SendAudioMessage.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/MessagingTestBed.cs tests/Modules/Messaging/Modules.Messaging.UnitTests/SendAudioMessageHandlerTests.cs
git commit -m "feat(messaging): handler de nota de voz con R2, subida a Meta y envío como voz"
git log -1 --format=%B
```

---

### Task 7: Endpoint, registro, integración, CSRF con auth real y README

**Files:**
- Modify: `src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs` (ruta después de la del texto, línea 46; delegado después de `SendMessageAsync`, línea 188)
- Modify: `src/Bootstrapper/QepServiceCollectionExtensions.cs:545`
- Modify: `README.md:794` y § «Mensajería (WhatsApp Cloud)», párrafo **Envío.** (línea ~1897)
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/OggOpusFixture.cs` (copia)
- Create: `tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendAudioMessageApiTests.cs`
- Modify: `tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs` (después de `MultipartImportWithoutCsrfHeaderIsRejected`, línea ~266)

**Interfaces:**
- Consumes: `SendAudioMessageCommand`, `SendAudioMessageHandler` (Tasks 2 y 6).
- Produces: `MessagingEndpoints.AudioBodyMaxBytes = 17L * 1024 * 1024` (`internal const long`); la ruta de §4.1.

- [ ] **Step 1: Copiar la nota de voz de prueba a IntegrationTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
Copy-Item "$B\tests\Modules\Messaging\Modules.Messaging.UnitTests\OggOpusFixture.cs" "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\OggOpusFixture.cs"
```

Con la herramienta de edición (no con `Set-Content`), cambiar en la copia `namespace Modules.Messaging.UnitTests;` por `namespace Modules.Messaging.IntegrationTests;` y, en el `<summary>`, «Copia en `Modules.Messaging.IntegrationTests`» por «Copia de la de `Modules.Messaging.UnitTests`».

- [ ] **Step 2: Escribir las pruebas que fallan**

`tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendAudioMessageApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §4.1, §6 y §10: multipart real con una nota OGG/Opus; <c>kind = 4</c>, la fila de
/// <c>message_media</c> con <c>stored_at</c> y el GET de media con los mismos bytes; validación por campo; 413 arriba
/// del límite (con y sin <c>Content-Length</c>); R2 caído es 422 sin fila. El CSRF con auth real vive en
/// <c>Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests</c> (este harness corre con el stub).</summary>
public sealed class SendAudioMessageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>R2 en un diccionario, como en <c>MediaApiTests</c>; <see cref="FailUploads"/> simula R2 caído.</summary>
    private sealed class FakeMediaStore : IMessagingMediaStore
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public bool FailUploads { get; set; }

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            if (FailUploads)
            {
                throw new IOException("R2 no responde");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = (buffer.ToArray(), contentType);
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Objects.TryGetValue(key, out var stored) ? new MediaStreamDto(new MemoryStream(stored.Bytes), stored.ContentType, stored.Bytes.Length, null) : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Sin <c>Content-Length</c>: <c>StreamContent</c> sobre un stream que no se puede recorrer viaja chunked.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMediaStore Store, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(store)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, host, store, connectionString, tenant, conversationId, client);
    }

    private static string AudioUrl(Fixture f) => MessagesUrl(f.Tenant.TenantId, f.ConversationId) + "/audio";

    private static void ScriptMetaOk(FakeMetaGraphHandler meta)
    {
        meta.Respond("/111/media$", HttpStatusCode.OK, """{"id":"media.out"}""");
        meta.Respond("/111/messages$", HttpStatusCode.OK, """{"messaging_product":"whatsapp","messages":[{"id":"wamid.voice"}]}""");
    }

    private static int MetaCalls(FakeMetaGraphHandler meta) =>
        meta.Requests.Count(request => request.Uri!.AbsolutePath.StartsWith("/v24.0/111/", StringComparison.Ordinal));

    private static MultipartFormDataContent VoiceForm(Guid? clientId, HttpContent file, string? replyTo = null)
    {
        var form = new MultipartFormDataContent();
        if (clientId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "clientId");
        }

        if (replyTo is not null)
        {
            form.Add(new StringContent(replyTo), "replyTo");
        }

        file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        form.Add(file, "file", "voice.ogg");
        return form;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AVoiceNoteAnswers201AsAudioStoresItPrivatelyAndServesTheSameBytes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var audio = OggOpusFixture.VoiceNote();
        var clientId = Guid.CreateVersion7();

        var response = await PostAsync(f.Client, AudioUrl(f), VoiceForm(clientId, new ByteArrayContent(audio)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var message = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = message.GetProperty("id").GetGuid();
        Assert.Equal(("Audio", "Sent", JsonValueKind.Null, clientId),
            (message.GetProperty("kind").GetString(), message.GetProperty("status").GetString(), message.GetProperty("text").ValueKind, message.GetProperty("clientId").GetGuid()));
        var media = message.GetProperty("media");
        Assert.Equal((MediaUrl(f.Tenant.TenantId, id), "audio/ogg", JsonValueKind.Null, JsonValueKind.Null),
            (media.GetProperty("url").GetString(), media.GetProperty("mimeType").GetString(), media.GetProperty("fileName").ValueKind, media.GetProperty("caption").ValueKind));

        // Meta: subida multipart y envío como nota de voz, con el token de la conexión.
        var upload = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/media");
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", upload.Authorization);
        Assert.Contains("voice.ogg", upload.Body, StringComparison.Ordinal);
        Assert.Contains("audio/ogg", upload.Body, StringComparison.Ordinal);
        var send = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath == "/v24.0/111/messages");
        Assert.Contains("\"audio\":{\"id\":\"media.out\",\"voice\":true}", send.Body, StringComparison.Ordinal);
        Assert.Contains($"\"biz_opaque_callback_data\":\"qep:{id}\"", send.Body, StringComparison.Ordinal);

        // Base: kind 4, medio guardado y fuera de la cola de copia, y la foto de la conversación como un audio entrante.
        Assert.Equal("4|true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT kind || '|' || (text IS NULL) FROM messaging.messages WHERE id = @id", ("id", id)));
        Assert.Equal($"media.out|messaging/{f.Tenant.TenantId}/{id}|{audio.Length}|{Convert.ToHexStringLower(SHA256.HashData(audio))}|true",
            await ScalarAsync<string>(f.ConnectionString,
                "SELECT meta_media_id || '|' || storage_key || '|' || size_bytes || '|' || sha256 || '|' || (stored_at IS NOT NULL) FROM messaging.message_media WHERE message_id = @id",
                ("id", id)));
        Assert.Equal("4|true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT last_message_kind || '|' || (last_message_preview IS NULL) FROM messaging.conversations WHERE id = @id", ("id", f.ConversationId)));

        // El GET de media de siempre devuelve los mismos bytes, inline.
        var served = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, id), Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("audio/ogg", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(audio, await served.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsTheStoredVoiceNoteWithoutUploadingAgain()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        var clientId = Guid.CreateVersion7();

        var first = await (await PostAsync(f.Client, AudioUrl(f), VoiceForm(clientId, new ByteArrayContent(OggOpusFixture.VoiceNote())))).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var again = await PostAsync(f.Client, AudioUrl(f), VoiceForm(clientId, new ByteArrayContent(OggOpusFixture.VoiceNote())));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        var second = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(("Audio", "audio/ogg"), (second.GetProperty("kind").GetString(), second.GetProperty("media").GetProperty("mimeType").GetString()));
        Assert.Equal(2, MetaCalls(f.Factory.MetaHandler));
    }

    // Review Focus 4 y 5: cada campo con su clave; un request que no es multipart es 422, no 415 ni 500.
    [Fact]
    public async Task TheFormIsValidatedFieldByField()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);

        var webm = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(OggOpusFixture.WebmHeader())));
        var noClient = await PostAsync(f.Client, AudioUrl(f), VoiceForm(null, new ByteArrayContent(OggOpusFixture.VoiceNote())));
        var badReply = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(OggOpusFixture.VoiceNote()), replyTo: "no-es-un-guid"));
        var json = await PostAsync(f.Client, AudioUrl(f), JsonContent.Create(new { clientId = Guid.CreateVersion7(), text = "hola" }));

        // ProblemAsync ordena las claves en ordinal: «clientId» antes que «file».
        foreach (var (response, keys) in new[] { (webm, new[] { "file" }), (noClient, new[] { "clientId" }), (badReply, new[] { "replyTo" }), (json, new[] { "clientId", "file" }) })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var (code, errorKeys) = await ProblemAsync(response);
            Assert.Equal("validation.failed", code);
            Assert.Equal(keys, errorKeys);
        }

        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
        Assert.Empty(f.Store.Objects);
    }

    [Fact]
    public async Task AnR2FailureIs422UploadFailedWithoutRowNorMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        ScriptMetaOk(f.Factory.MetaHandler);
        f.Store.FailUploads = true;

        var response = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(OggOpusFixture.VoiceNote())));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.media.upload_failed", (await ProblemAsync(response)).Code);
        Assert.Equal(0, MetaCalls(f.Factory.MetaHandler));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    [Fact]
    public async Task ABodyOverTheLimitIs413()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var tooBig = new byte[(17 * 1024 * 1024) + 1];
        OggOpusFixture.VoiceNote().CopyTo(tooBig, 0);

        var response = await PostAsync(f.Client, AudioUrl(f), VoiceForm(Guid.CreateVersion7(), new ByteArrayContent(tooBig)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE direction = 2"));
    }

    // Review Focus 3: sin Content-Length el tope lo pone MultipartBodyLengthLimit; tiene que ser 413, no 500.
    [Fact]
    public async Task AChunkedBodyOverTheLimitIs413()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var tooBig = new byte[(17 * 1024 * 1024) + 1];
        OggOpusFixture.VoiceNote().CopyTo(tooBig, 0);
        var form = VoiceForm(Guid.CreateVersion7(), new StreamContent(new UnseekableStream(tooBig)));
        Assert.Null(form.Headers.ContentLength);

        var response = await PostAsync(f.Client, AudioUrl(f), form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
```

En `RealAuthenticationApiTests.cs`, después de `MultipartImportWithoutCsrfHeaderIsRejected` y su `ImportUpload`:

```csharp
    // Spec 2026-10-11 §6.1: la nota de voz lleva DisableAntiforgery por el mismo motivo que /import, y un multipart POST
    // es un request simple, sin preflight. Sólo X-Qep-Client lo frena. La defensa corre antes de la autenticación.
    [Fact]
    public async Task VoiceNoteWithoutCsrfHeaderIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var url = $"/api/v1/tenants/{Guid.NewGuid()}/messaging/conversations/{Guid.NewGuid()}/messages/audio";

        using var withoutHeader = new HttpRequestMessage(HttpMethod.Post, url) { Content = VoiceUpload() };
        var rejected = await client.SendAsync(withoutHeader, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("Missing required client header.", problem?.Title);

        using var withHeader = new HttpRequestMessage(HttpMethod.Post, url) { Content = VoiceUpload() };
        withHeader.Headers.Add("X-Qep-Client", "web");
        var passed = await client.SendAsync(withHeader, TestContext.Current.CancellationToken);

        // Pasó la defensa CSRF y la frenó la autenticación: no hay sesión.
        Assert.Equal(HttpStatusCode.Unauthorized, passed.StatusCode);
    }

    private static MultipartFormDataContent VoiceUpload()
    {
        var content = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "clientId" } };
        var file = new ByteArrayContent("OggS"u8.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/ogg");
        content.Add(file, "file", "voice.ogg");
        return content;
    }
```

- [ ] **Step 3: Correr y verificar que fallan**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~SendAudioMessageApiTests"
dotnet test "$B\tests\Modules\Tenancy\Modules.Tenancy.IntegrationTests\Modules.Tenancy.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~RealAuthenticationApiTests.VoiceNoteWithoutCsrfHeaderIsRejected"
```

Esperado: las de `SendAudioMessageApiTests` fallan con `Expected: Created / UnprocessableEntity / RequestEntityTooLarge`, `Actual: NotFound` (la ruta no existe). La de CSRF probablemente pasa el 403 (el middleware corre para toda ruta) y falla en `Expected: Unauthorized, Actual: NotFound`; si pasara entera en verde, se anota en el handoff: queda como candado de regresión y el RED de la tarea lo dan las de Messaging.

- [ ] **Step 4: Implementar el endpoint**

`MessagingEndpoints.cs`, agregar a los `using`:

```csharp
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
```

Dentro de la clase, arriba de `MapMessagingEndpoints`:

```csharp
    /// <summary>Spec 2026-10-11 §6.1: 16 MB de audio más lo que ocupa el multipart (límites, cabeceras de cada parte y
    /// los dos campos de texto). Sólo para esta ruta: el resto de la API conserva el suyo (§9).</summary>
    internal const long AudioBodyMaxBytes = 17L * 1024 * 1024;
```

Después de la ruta del texto (línea 46):

```csharp
        // Spec 2026-10-11 §4.1: nota de voz en multipart. Ruta propia, para que el Accepts del texto siga en JSON.
        // DisableAntiforgery con el criterio de CustomerEndpoints (/import); el CSRF por X-Qep-Client sigue aplicando.
        group.MapPost("/conversations/{conversationId:guid}/messages/audio", SendAudioMessageAsync)
            .RequireAuthorization(MessagingPermissions.ConversationManage)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();
```

Después de `SendMessageAsync`:

```csharp
    /// <summary>
    /// Spec 2026-10-11 §6.1. El form se lee a mano y no con un <c>IFormFile</c> en la firma: el binder lo leería entero
    /// antes de entrar acá, con el tope global, y fijar el del endpoint ya no serviría. Primero el tope, después el form.
    /// Un <c>Content-Length</c> declarado de más se corta sin leer; uno chunked lo corta <c>MultipartBodyLengthLimit</c>.
    /// Lo que falte o no parsee llega como <c>null</c> (o <c>Guid.Empty</c> en <c>replyTo</c>) y lo marca el validador
    /// con su clave: un form sin <c>file</c> o un cuerpo que no es multipart es 422, no 415.
    /// </summary>
    private static async Task<IResult> SendAudioMessageAsync(
        Guid tenantId, Guid conversationId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = AudioBodyMaxBytes;
        }

        var request = httpContext.Request;
        if (request.ContentLength > AudioBodyMaxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        IFormCollection form;
        try
        {
            form = request.HasFormContentType
                ? await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = AudioBodyMaxBytes }, cancellationToken)
                : FormCollection.Empty;
        }
        catch (InvalidDataException)
        {
            // MultipartBodyLengthLimit superado en un cuerpo sin Content-Length.
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (BadHttpRequestException exception)
        {
            // Lo que Kestrel corta al leer (el tope superado, o un cuerpo más corto que su Content-Length): un request malo,
            // no una falla nuestra. Sin esto ApiExceptionHandler lo volvería un 500 (mismo criterio que WebhookBodyReader).
            return Results.StatusCode(exception.StatusCode);
        }

        var command = new SendAudioMessageCommand(
            tenantId, conversationId, ParseClientId(form["clientId"]), await ReadAudioAsync(form.Files.GetFile("file"), cancellationToken), ParseReplyTo(form["replyTo"]));
        var message = await dispatcher.SendAsync(command, cancellationToken);
        // Sin Location, igual que el texto.
        return Results.Created((string?)null, message);
    }

    // ≤ 17 MB por el tope del cuerpo; el validador decide si cabe en los 16 MB de Meta.
    private static async Task<byte[]?> ReadAudioAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return null;
        }

        using var buffer = new MemoryStream(capacity: (int)Math.Min(file.Length, AudioBodyMaxBytes));
        await file.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static Guid? ParseClientId(StringValues value) => Guid.TryParse(value.ToString(), out var id) ? id : null;

    /// <summary>Ausente o vacío: sin cita. Presente y no GUID: <c>Guid.Empty</c>, para que el validador lo marque en
    /// <c>errors.replyTo</c> en vez de ignorar la cita en silencio.</summary>
    private static Guid? ParseReplyTo(StringValues value) =>
        StringValues.IsNullOrEmpty(value) ? null : Guid.TryParse(value.ToString(), out var id) ? id : Guid.Empty;
```

Actualizar el `<summary>` de `MapMessagingEndpoints`: «… y envío (Task 16), nota de voz (spec 2026-10-11), leído, …».

`QepServiceCollectionExtensions.cs`, debajo de la línea 545:

```csharp
        // Messaging (spec 2026-10-11 §6): la nota de voz, con las mismas guardas que el texto.
        services.AddScoped<ICommandHandler<SendAudioMessageCommand, MessageDto>, SendAudioMessageHandler>();
```

- [ ] **Step 5: README**

`README.md:794`, en la fila de `/api/v1/tenants/{tenantId}/messaging`, cambiar `messages` (`GET`, `POST`), por `messages` (`GET`, `POST`), `messages/audio` (`POST`, multipart),.

En § «Mensajería (WhatsApp Cloud)», después del párrafo **Envío.**:

```markdown
**Notas de voz.** `POST …/conversations/{id}/messages/audio` en `multipart/form-data` con `clientId`, `file` y
`replyTo` opcional (spec 2026-10-11). Mismas reglas que el texto: ventana, asignado, autoasignación, cita e
idempotencia por `clientId`. El archivo se valida **por contenido** (OGG con `OpusHead`; un WebM o un MP4 es 422 en
`file`), hasta 16 MB; el cuerpo tiene tope propio de 17 MB (413). Dentro del reclamo: R2 (`messaging/{tenantId}/{messageId}`,
bucket privado) → `POST /{phone_number_id}/media` → envío con `audio.voice = true`, que es lo que hace que llegue como
nota de voz. R2 caído es 422 `messaging.media.upload_failed` sin fila. La fila `Failed` guarda su `message_media`
(con `meta_media_id = ''` si Meta nunca dio un id), así que la nota se escucha desde otro dispositivo y el reintento
sobrescribe la misma clave.
```

- [ ] **Step 6: Correr y verificar que pasan, más ArchitectureTests**

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\tests\Modules\Messaging\Modules.Messaging.IntegrationTests\Modules.Messaging.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~SendAudioMessageApiTests|FullyQualifiedName~SendMessageApiTests|FullyQualifiedName~MediaApiTests"
dotnet test "$B\tests\Modules\Tenancy\Modules.Tenancy.IntegrationTests\Modules.Tenancy.IntegrationTests.csproj" --no-build --filter "FullyQualifiedName~RealAuthenticationApiTests"
dotnet test "$B\tests\ArchitectureTests\ArchitectureTests\ArchitectureTests.csproj" --no-build --filter "FullyQualifiedName~MessagingLayerTests"
```

Esperado: todo verde. Si `AChunkedBodyOverTheLimitIs413` da 500, mirar en el log de la prueba qué excepción salió de `ReadFormAsync` (puede venir envuelta en `IOException`) y agregar ese tipo al `catch` que devuelve 413; no se toca la prueba.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/andre/OneDrive/Documentos2/repositories/QCode/templates/qep/qep-backend
[ "$(git branch --show-current)" = "feature/notas-de-voz" ] || exit 1
git status --short
git add src/Modules/Messaging/Modules.Messaging.Api/MessagingEndpoints.cs src/Bootstrapper/QepServiceCollectionExtensions.cs README.md tests/Modules/Messaging/Modules.Messaging.IntegrationTests/OggOpusFixture.cs tests/Modules/Messaging/Modules.Messaging.IntegrationTests/SendAudioMessageApiTests.cs tests/Modules/Tenancy/Modules.Tenancy.IntegrationTests/RealAuthenticationApiTests.cs
git commit -m "feat(messaging): endpoint multipart de nota de voz con tope de 17 MB"
git log -1 --format=%B
```

---

### Task 8: Suite completa, una sola vez

**Files:** ninguno (salvo que algo falle por este trabajo).

- [ ] **Step 1: Build y suite completa en primer plano**

Comprobar antes que no corre `Api.exe` (ver «Antes de empezar»).

```powershell
$B = "C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend"
dotnet restore "$B\Backend.slnx" --locked-mode
if ($LASTEXITCODE -ne 0) { throw "restore falló" }
dotnet build "$B\Backend.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { throw "build falló" }
dotnet test "$B\Backend.slnx" --no-build
```

`--locked-mode` confirma que no se movió ningún `packages.lock.json` (no hay paquetes nuevos). Copiar al handoff el resumen literal por proyecto.

- [ ] **Step 2: Si algo falla, separar lo previo de lo nuevo, por nombre**

Una falla en una clase que este plan tocó (`SendMessage*`, `SendAudio*`, `OutboundAudio*`, `WhatsAppCloudClientTests`, `MediaApiTests`, `RealAuthenticationApiTests`, `MessagingLayerTests`, `OggOpus*`, `MessagingMediaKeys*`) es de este trabajo: se arregla en la tarea que corresponde (con su RED/GREEN) y se vuelve a correr **sólo** esa clase. Una falla en otra clase se compara **por nombre de prueba** contra `develop` (`468997f`) corriendo sólo esa clase en un worktree temporal hermano (nunca bajo `/tmp`), por ejemplo `C:\Users\andre\OneDrive\Documentos2\repositories\QCode\templates\qep\qep-backend-worktrees\baseline-notas-de-voz`; si también falla ahí, es previa (ya hay dos conocidas en `OrderExportApiTests`) y se anota en el handoff sin tocarla. El worktree temporal se quita al terminar (si `git worktree remove` falla con «Invalid argument», es `codegraph serve` con el índice abierto: cerrarlo primero).

- [ ] **Step 3: Handoff**

Sin commit en este paso. El handoff lleva: los ocho commits (`git log --oneline 468997f..HEAD`), el resumen literal de la suite, las fallas previas por nombre si las hubo, y los «Desvíos del spec» de arriba para que el owner los ratifique junto con D-V1 a D-V4. Despliegue (spec §11): backend primero, frontend después; sin migración ni configuración nueva.
