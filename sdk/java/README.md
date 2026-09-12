# MAIL Java SDK

Java 17+ SDK for MAIL integration protocol v1. Maven coordinates are
`dev.mail:mail-sdk:0.1.0-SNAPSHOT` (local development; not published to Maven Central).
Jackson is the only direct runtime dependency. No JNI or global PATH modification.

## Usage

Build/install locally with `mvn clean install` in this directory, then add:

```xml
<dependency>
  <groupId>dev.mail</groupId>
  <artifactId>mail-sdk</artifactId>
  <version>0.1.0-SNAPSHOT</version>
</dependency>
```

```java
import com.fasterxml.jackson.databind.ObjectMapper;
import dev.mail.sdk.MailRuntime;
import java.nio.file.Path;

var json = new ObjectMapper();
try (var runtime = MailRuntime.start()) {
    runtime.registerTool("Echo", input -> input);
    var input = json.createObjectNode().put("text", "Olá do Java!");
    var result = runtime.run(Path.of("examples/docs/tools.mail"), input).join();
    System.out.println(result.path("text").asText());
}
```

`start()` uses a CLI bundled in the JAR. The ordinary development build does **not**
include a binary: use `MailRuntime.start(Path.of("/absolute/path/to/Mail.Cli.exe"))`
until you build the bundled artifact below. Resolution order:

1. Explicit `Path` supplied to `start`.
2. JVM property `-Dmail.cli.path=/absolute/path/to/CLI`.
3. Environment variable `MAIL_EXE`.
4. Platform ZIP embedded in the SDK JAR.

There is no PATH search or runtime download. The bundled CLI is extracted to a
private temporary directory, launched with an absolute path and cleaned up when
the runtime closes. Close every runtime with try-with-resources. Each runtime
owns its own process and extraction directory; multiple runs share that process.
Paths containing spaces are passed as separate process arguments. The subprocess
inherits the environment and working directory, so credential/.env behavior stays
consistent with the CLI. Do not put credentials in the distributed CLI bundle.

## API

- `MailRuntime.start()` / `start(Path)` starts the process and performs handshake.
- `start(Path, Duration handshakeTimeout, Duration loadTimeout)` customizes timeouts
  (defaults: 10 and 30 seconds).
- `registerTool(String, Tool)` accepts a synchronous callback returning `JsonNode`.
- `registerAsyncTool(String, AsyncTool)` accepts a callback returning
  `CompletionStage<JsonNode>`.
- `load(Path)` returns `CompletableFuture<JsonNode>` containing the tool contract
  array, including extended fields delivered by the CLI.
- `run(Path, JsonNode input[, JsonNode providerConfig])` loads the program, checks
  all declared tools are registered and executes. Output can be an object, array,
  scalar or JSON null. Providers are declared in the `.mail` program; optional
  `provider_config` is passed through unchanged.
- `close()` closes the session, rejects outstanding operations and terminates the
  subprocess. Closed runtimes cannot be restarted.

Callbacks run outside the protocol reader. Shared tool state must be thread-safe.
Registering the same name replaces the implementation. Async callbacks may use
your own executor. Callback exceptions become `tool_error` responses.

Cancel the **original future returned by `run`** with `future.cancel(true)` to
request remote cancellation. Cancelling a dependent future does not propagate.
Cancellation completes the local future immediately; the server may still be
finishing. Running callbacks and external effects cannot be forcibly undone.
No client execution timeout is imposed; MAIL's own execution limits still apply.
Use async completion methods for blocking application continuations.

`MailException` contains typed nested exceptions: `Process`, `Protocol`, `Load`,
`Run`, and `Tool`. `Load.diagnostics()` retains compiler diagnostics as JSON.
Asynchronous failures are wrapped by `ExecutionException` (`get`) or
`CompletionException` (`join`). Startup failures throw directly.
Uncorrelated protocol errors terminate the session instead of leaving requests
pending. Unknown notifications are ignored. Protocol events are currently ignored.

## Bundle the CLI for installation-only usage

Publish a **self-contained** CLI from the same source revision being released with
the SDK. Example from the repository root (requires the project's .NET SDK):

```powershell
dotnet publish src/Mail.Cli/Mail.Cli.csproj -c Release -r win-x64 --self-contained true -o sdk/java/target/cli-win-x64
./sdk/java/scripts/bundle-cli.ps1 -Platform win-x64 -PublishDirectory sdk/java/target/cli-win-x64
cd sdk/java
mvn clean install -Pbundled-cli
```

The script creates `native-bundles/win-x64.zip`, retaining the publish directory's
sidecar dependencies. ZIP entries must have `Mail.Cli.exe` (Windows) or `Mail.Cli`
(Unix) at their root. The Maven profile embeds ZIPs under `dev/mail/sdk/native/`.
Use `-Dmail.cli.bundles=/path/to/bundles` to override the source directory.
Always use `clean` when changing between bundled and unbundled builds to avoid
stale resources. Bundles and generated JARs are excluded from version control.

The resolver recognizes Windows/Linux/macOS x64 and arm64. Only platforms actually
included in the JAR are available automatically. Linux bundles must match the
target libc/runtime environment; this initial distribution does not distinguish
musl/Alpine. Validate each supported target before publishing it. Windows x64 is
the locally tested target. For public releases, decide the final Maven namespace,
publish immutable SDK versions with their matching binaries and run the bundled
smoke test for every shipped platform. No Maven Central publication is configured.

## Tests

```powershell
mvn test '-Dmail.test.cli=C:/absolute/path/to/Mail.Cli.exe'
```

Integration tests launch the real CLI and use direct tools without a model, API
keys or network calls. They cover Unicode, spaces, concurrent executions, async
callbacks, load diagnostics, tool errors, cancellation and shutdown. Without
`mail.test.cli` or `MAIL_EXE`, these integration tests are explicitly skipped.

`src/test/java/dev/mail/sdk/BundledSmoke.java` is a standalone packaged-JAR check.
After building with `-Pbundled-cli`, run it in a fresh JVM with the SDK JAR, Jackson
dependencies and `target/test-classes` on the classpath, passing the absolute path
of `examples/docs/tools.mail`. Clear `MAIL_EXE` and `mail.cli.path` and omit
`target/classes` to verify the executable is really resolved from the JAR.
