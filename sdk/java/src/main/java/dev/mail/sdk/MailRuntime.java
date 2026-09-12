package dev.mail.sdk;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;

/** Owns one MAIL CLI process and multiplexes protocol-v1 executions. Java 17+. */
public final class MailRuntime implements AutoCloseable {
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final int MAX_LINE = 4 * 1024 * 1024;
    private final Object wireLock = new Object();
    private final java.lang.Process process;
    private final OutputStream stdin;
    private final CliLocator cli;
    private final ExecutorService callbacks = Executors.newCachedThreadPool(r -> {
        Thread thread = new Thread(r, "mail-java-callback");
        thread.setDaemon(true);
        return thread;
    });
    private final ConcurrentMap<String, AsyncTool> tools = new ConcurrentHashMap<>();
    private final Map<String, CompletableFuture<JsonNode>> loads = new HashMap<>();
    private final Map<String, RunState> runs = new HashMap<>();
    private final CompletableFuture<Void> handshake = new CompletableFuture<>();
    private volatile MailException failure;
    private boolean closed;
    private final Duration loadTimeout;

    private static final class RunState {
        final String id = UUID.randomUUID().toString();
        final CompletableFuture<JsonNode> result = new CompletableFuture<>();
        boolean sent;
    }

    /** Uses the bundled CLI, with optional mail.cli.path / MAIL_EXE overrides. */
    public static MailRuntime start() { return start((Path) null); }

    public static MailRuntime start(Path executable) {
        return start(executable, Duration.ofSeconds(10), Duration.ofSeconds(30));
    }

    public static MailRuntime start(Path executable, Duration handshakeTimeout, Duration loadTimeout) {
        return new MailRuntime(executable, handshakeTimeout, loadTimeout);
    }

    private MailRuntime(Path executable, Duration handshakeTimeout, Duration loadTimeout) {
        positive(handshakeTimeout);
        positive(loadTimeout);
        this.loadTimeout = loadTimeout;
        cli = CliLocator.resolve(executable);
        try {
            process = new ProcessBuilder(cli.executable.toString(), "integrate")
                .redirectError(ProcessBuilder.Redirect.INHERIT).start();
            stdin = process.getOutputStream();
        } catch (IOException error) {
            cli.close();
            callbacks.shutdownNow();
            throw new MailException.Process("Cannot start MAIL CLI: " + cli.executable, error);
        }
        Thread reader = new Thread(this::readLoop, "mail-java-reader");
        reader.setDaemon(true);
        reader.start();
        try {
            send(message("handshake").put("protocol_version", 1).put("client", "java-sdk"));
            handshake.get(handshakeTimeout.toMillis(), TimeUnit.MILLISECONDS);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            close();
            throw new MailException.Process("MAIL handshake interrupted", error);
        } catch (ExecutionException | TimeoutException | RuntimeException error) {
            close();
            if (error instanceof ExecutionException && error.getCause() instanceof MailException mail) throw mail;
            if (error instanceof MailException mail) throw mail;
            throw new MailException.Process("MAIL handshake failed or timed out", error);
        }
    }

    private static void positive(Duration duration) {
        if (duration == null || duration.toMillis() <= 0) throw new IllegalArgumentException("Timeout must be at least 1 ms");
    }

    public void registerTool(String name, Tool tool) {
        Objects.requireNonNull(tool, "tool");
        registerAsyncTool(name, input -> CompletableFuture.completedFuture(tool.execute(input)));
    }

    public void registerAsyncTool(String name, AsyncTool tool) {
        if (name == null || name.isBlank()) throw new IllegalArgumentException("Tool name must not be blank");
        tools.put(name, Objects.requireNonNull(tool, "tool"));
    }

    /** Compiles a file and returns its tools, preserving extended/unknown metadata. */
    public CompletableFuture<JsonNode> load(Path path) {
        String absolute = Objects.requireNonNull(path, "path").toAbsolutePath().normalize().toString();
        String id = UUID.randomUUID().toString();
        CompletableFuture<JsonNode> result = new CompletableFuture<>();
        synchronized (wireLock) {
            ensureRunning();
            loads.put(id, result);
            result.whenComplete((value, error) -> { synchronized (wireLock) { loads.remove(id); } });
            try { send(message("load").put("load_id", id).put("path", absolute)); }
            catch (RuntimeException error) { result.completeExceptionally(error); }
        }
        CompletableFuture.delayedExecutor(loadTimeout.toMillis(), TimeUnit.MILLISECONDS).execute(() ->
            result.completeExceptionally(new MailException.Process("MAIL load timed out after " + loadTimeout)));
        return result;
    }

    public CompletableFuture<JsonNode> run(Path path, JsonNode input) { return run(path, input, null); }

    /**
     * Loads, checks local tool registrations, then executes. Output may be any JSON value.
     * Cancel the returned future to request remote cancellation. Effects are not rolled back.
     */
    public CompletableFuture<JsonNode> run(Path path, JsonNode input, JsonNode providerConfig) {
        Objects.requireNonNull(input, "input");
        String absolute = Objects.requireNonNull(path, "path").toAbsolutePath().normalize().toString();
        ObjectNode request = message("run").put("path", absolute);
        request.set("input", input.deepCopy());
        if (providerConfig != null) request.set("provider_config", providerConfig.deepCopy());
        RunState state = new RunState();
        request.put("execution_id", state.id);
        synchronized (wireLock) {
            ensureRunning();
            runs.put(state.id, state);
        }
        state.result.whenComplete((value, error) -> {
            synchronized (wireLock) {
                runs.remove(state.id);
                if (state.result.isCancelled() && state.sent) {
                    try { send(message("cancel").put("execution_id", state.id)); }
                    catch (MailException ignored) { /* already disconnected */ }
                }
            }
        });
        CompletableFuture<JsonNode> loading;
        try { loading = load(Path.of(absolute)); }
        catch (RuntimeException error) { state.result.completeExceptionally(error); return state.result; }
        state.result.whenComplete((value, error) -> { if (state.result.isCancelled()) loading.cancel(false); });
        loading.whenComplete((contracts, error) -> {
            synchronized (wireLock) {
                if (state.result.isDone()) return;
                if (error != null) { state.result.completeExceptionally(error); return; }
                List<String> missing = new ArrayList<>();
                for (JsonNode contract : contracts) {
                    String name = contract.path("name").asText();
                    if (!tools.containsKey(name)) missing.add(name);
                }
                if (!missing.isEmpty()) {
                    state.result.completeExceptionally(new MailException.Tool("No registered tool(s): " + missing));
                    return;
                }
                try {
                    // Serialize run and cancellation writes under the same lock.
                    send(request);
                    state.sent = true;
                } catch (RuntimeException ex) { state.result.completeExceptionally(ex); }
            }
        });
        return state.result;
    }

    private static ObjectNode message(String type) { return JSON.createObjectNode().put("type", type); }

    private void ensureRunning() {
        if (failure != null) throw failure;
        if (closed) throw new MailException.Process("MAIL runtime is closed");
    }

    private void send(ObjectNode message) {
        synchronized (wireLock) {
            ensureRunning();
            try {
                byte[] data = JSON.writeValueAsBytes(message);
                if (data.length > MAX_LINE) throw new MailException.Protocol("MAIL message exceeds 4 MiB");
                stdin.write(data);
                stdin.write('\n');
                stdin.flush();
            } catch (IOException error) {
                MailException failure = new MailException.Process("Cannot write to MAIL CLI", error);
                fail(failure);
                throw failure;
            }
        }
    }

    private void readLoop() {
        try (InputStream stdout = new BufferedInputStream(process.getInputStream())) {
            ByteArrayOutputStream line = new ByteArrayOutputStream();
            int next;
            while ((next = stdout.read()) != -1) {
                if (next == '\n') {
                    String text = line.toString(StandardCharsets.UTF_8);
                    line.reset();
                    if (!text.isBlank()) {
                        JsonNode msg = JSON.readTree(text);
                        if (msg == null || !msg.isObject() || !msg.path("type").isTextual())
                            throw new MailException.Protocol("Invalid MAIL protocol message");
                        dispatch(msg);
                    }
                } else {
                    if (line.size() >= MAX_LINE) throw new MailException.Protocol("MAIL response exceeds 4 MiB");
                    line.write(next);
                }
            }
            fail(new MailException.Process("MAIL CLI closed stdout unexpectedly"));
        } catch (Exception error) {
            fail(error instanceof MailException mail ? mail : new MailException.Process("MAIL protocol reader failed", error));
        }
    }

    private void dispatch(JsonNode msg) {
        switch (msg.path("type").asText()) {
            case "handshake_ok" -> {
                if (msg.path("protocol_version").asInt() != 1) fail(new MailException.Protocol("Unsupported MAIL protocol version"));
                else handshake.complete(null);
            }
            case "handshake_error" -> fail(new MailException.Protocol(msg.path("reason").asText()));
            case "loaded", "load_error" -> {
                CompletableFuture<JsonNode> pending;
                synchronized (wireLock) { pending = loads.get(msg.path("load_id").asText()); }
                if (pending != null) completeOffReader(() -> {
                    if (msg.path("type").asText().equals("loaded")) pending.complete(msg.path("tools"));
                    else pending.completeExceptionally(new MailException.Load(msg.path("diagnostics")));
                });
            }
            case "result", "run_error" -> {
                RunState state;
                synchronized (wireLock) { state = runs.get(msg.path("execution_id").asText()); }
                if (state != null) completeOffReader(() -> {
                    if (msg.path("type").asText().equals("result")) state.result.complete(msg.path("output"));
                    else state.result.completeExceptionally(new MailException.Run(msg.path("error").asText()));
                });
            }
            case "tool_request" -> completeOffReader(() -> handleTool(msg));
            case "protocol_error" -> {
                // Nonfatal errors cannot be correlated reliably; fail instead of hanging a run.
                fail(new MailException.Protocol(msg.path("reason").asText()));
            }
            case "run_started", "event" -> { /* v1 batches events; no event API yet */ }
            default -> { /* tolerate future protocol notifications */ }
        }
    }

    private void completeOffReader(Runnable action) {
        try { callbacks.execute(action); }
        catch (RejectedExecutionException ignored) { /* shutdown has rejected pending requests */ }
    }

    private void handleTool(JsonNode msg) {
        String requestId = msg.path("request_id").asText();
        synchronized (wireLock) {
            RunState state = runs.get(msg.path("execution_id").asText());
            if (state == null || state.result.isDone()) return;
        }
        try {
            AsyncTool tool = tools.get(msg.path("tool").asText());
            if (tool == null) throw new MailException.Tool("No registered tool: " + msg.path("tool").asText());
            Objects.requireNonNull(tool.execute(msg.path("input")), "Async tool returned null stage")
                .whenComplete((output, error) -> {
                    if (error != null) toolError(requestId, error);
                    else {
                        ObjectNode reply = message("tool_response").put("request_id", requestId);
                        reply.set("output", output == null ? JSON.nullNode() : output);
                        try { send(reply); }
                        catch (MailException.Protocol ex) { toolError(requestId, ex); }
                        catch (MailException ignored) { /* runtime closed */ }
                    }
                });
        } catch (Exception error) { toolError(requestId, error); }
    }

    private void toolError(String id, Throwable error) {
        String reason = error.getMessage() == null ? error.getClass().getSimpleName() : error.getMessage();
        // Bound callback errors so even a huge exception can be reported on the wire.
        if (reason.length() > 8192) reason = reason.substring(0, 8192);
        try { send(message("tool_error").put("request_id", id).put("error", reason)); }
        catch (MailException ignored) { /* runtime closed */ }
    }

    private void fail(MailException error) {
        List<CompletableFuture<JsonNode>> pending = new ArrayList<>();
        synchronized (wireLock) {
            if (failure != null) return;
            failure = error;
            pending.addAll(loads.values());
            runs.values().forEach(run -> pending.add(run.result));
            loads.clear();
            runs.clear();
        }
        handshake.completeExceptionally(error);
        pending.forEach(future -> future.completeExceptionally(error));
        process.destroy();
    }

    @Override public void close() {
        synchronized (wireLock) {
            if (closed) return;
            closed = true;
            try { stdin.close(); } catch (IOException ignored) { }
        }
        fail(new MailException.Process("MAIL runtime closed"));
        callbacks.shutdownNow();
        try {
            if (!process.waitFor(5, TimeUnit.SECONDS)) {
                process.destroyForcibly();
                process.waitFor(5, TimeUnit.SECONDS);
            }
        } catch (InterruptedException error) {
            process.destroyForcibly();
            Thread.currentThread().interrupt();
        } finally { cli.close(); }
    }
}
