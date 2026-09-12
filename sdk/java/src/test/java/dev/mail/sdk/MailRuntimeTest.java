package dev.mail.sdk;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;
import org.junit.jupiter.api.io.TempDir;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.jupiter.api.Assertions.*;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

@Timeout(30)
class MailRuntimeTest {
    private static final ObjectMapper JSON = new ObjectMapper();
    @TempDir Path temp;

    private MailRuntime runtime() {
        String configured = System.getProperty("mail.test.cli", System.getenv("MAIL_EXE"));
        assumeTrue(configured != null, "Set -Dmail.test.cli to run real CLI integration tests");
        return MailRuntime.start(Path.of(configured));
    }

    private Path program() throws Exception {
        Path path = temp.resolve("tool with spaces.mail");
        Files.writeString(path, """
            schema Request { text: String }
            schema Reply { text: String }
            tool Echo { input { text: String } output { text: String } }
            workflow DirectTool {
                input Request
                output Reply
                step echo { call Echo { text: input.text } save as reply }
                finish with reply
            }
            """);
        return path;
    }

    private JsonNode input(String text) { return JSON.createObjectNode().put("text", text); }

    @Test void roundTripUnicodeAndConcurrentRuns() throws Exception {
        Path path = program();
        try (var runtime = runtime()) {
            runtime.registerTool("Echo", args -> input(args.path("text").asText() + " ✓"));
            List<CompletableFuture<JsonNode>> runs = new ArrayList<>();
            for (int i = 0; i < 12; i++) runs.add(runtime.run(path, input("Olá 🚀 " + i)));
            for (int i = 0; i < runs.size(); i++)
                assertEquals("Olá 🚀 " + i + " ✓", runs.get(i).get(15, TimeUnit.SECONDS).path("text").asText());
        }
    }

    @Test void asyncToolAndLoadContracts() throws Exception {
        try (var runtime = runtime()) {
            Path path = program();
            JsonNode contracts = runtime.load(path).get(10, TimeUnit.SECONDS);
            assertEquals("Echo", contracts.get(0).path("name").asText());
            runtime.registerAsyncTool("Echo", args -> CompletableFuture.supplyAsync(() -> args));
            assertEquals(input("hello"), runtime.run(path, input("hello")).get(10, TimeUnit.SECONDS));
        }
    }

    @Test void missingToolFailsBeforeRun() throws Exception {
        try (var runtime = runtime()) {
            var error = assertThrows(ExecutionException.class, () -> runtime.run(program(), input("x")).get());
            assertInstanceOf(MailException.Tool.class, error.getCause());
        }
    }

    @Test void callbackErrorPropagates() throws Exception {
        try (var runtime = runtime()) {
            runtime.registerTool("Echo", args -> { throw new IllegalStateException("callback failed"); });
            var error = assertThrows(ExecutionException.class, () -> runtime.run(program(), input("x")).get());
            assertInstanceOf(MailException.Run.class, error.getCause());
            assertTrue(error.getCause().getMessage().contains("callback failed"));
        }
    }

    @Test void compilationDiagnostics() throws Exception {
        Path invalid = temp.resolve("invalid.mail");
        Files.writeString(invalid, "not a MAIL program!!!");
        try (var runtime = runtime()) {
            var error = assertThrows(ExecutionException.class, () -> runtime.run(invalid, input("x")).get());
            var load = assertInstanceOf(MailException.Load.class, error.getCause());
            assertFalse(load.diagnostics().isEmpty());
        }
    }

    @Test void cancellationDoesNotBlockOtherRuns() throws Exception {
        try (var runtime = runtime()) {
            CountDownLatch entered = new CountDownLatch(1);
            CompletableFuture<JsonNode> blocked = new CompletableFuture<>();
            runtime.registerAsyncTool("Echo", args -> {
                if (args.path("text").asText().equals("blocked")) { entered.countDown(); return blocked; }
                return CompletableFuture.completedFuture(args);
            });
            Path path = program();
            var run = runtime.run(path, input("blocked"));
            assertTrue(entered.await(10, TimeUnit.SECONDS));
            assertTrue(run.cancel(true));
            assertThrows(CancellationException.class, run::join);
            assertEquals(input("next"), runtime.run(path, input("next")).get(10, TimeUnit.SECONDS));
            blocked.complete(input("late"));
        }
    }

    @Test void closeRejectsPendingAndFurtherWork() throws Exception {
        var runtime = runtime();
        try {
            CountDownLatch entered = new CountDownLatch(1);
            runtime.registerAsyncTool("Echo", args -> { entered.countDown(); return new CompletableFuture<>(); });
            var run = runtime.run(program(), input("x"));
            assertTrue(entered.await(10, TimeUnit.SECONDS));
            runtime.close();
            var error = assertThrows(ExecutionException.class, () -> run.get(2, TimeUnit.SECONDS));
            assertInstanceOf(MailException.Process.class, error.getCause());
            assertThrows(MailException.Process.class, () -> runtime.run(Path.of("anything.mail"), input("x")));
        } finally { runtime.close(); }
    }

    @Test void missingExecutableIsActionable() {
        assertThrows(MailException.Process.class, () -> MailRuntime.start(temp.resolve("missing.exe")));
    }

    @Test void scalarOutputIsPreserved() throws Exception {
        Path path = temp.resolve("scalar.mail");
        Files.writeString(path, """
            schema Request { value: Int }
            workflow Scalar { input Request output Int finish with input.value }
            """);
        try (var runtime = runtime()) {
            var result = runtime.run(path, JSON.createObjectNode().put("value", 42)).get(10, TimeUnit.SECONDS);
            assertTrue(result.isIntegralNumber());
            assertEquals(42, result.intValue());
        }
    }

    @Test void invalidToolOutputFailsValidation() throws Exception {
        try (var runtime = runtime()) {
            runtime.registerTool("Echo", args -> JSON.createObjectNode().put("wrong", 42));
            var error = assertThrows(ExecutionException.class, () -> runtime.run(program(), input("x")).get());
            assertInstanceOf(MailException.Run.class, error.getCause());
        }
    }

    @Test void processDeathRejectsPendingRun() throws Exception {
        Set<Long> existing = new HashSet<>();
        ProcessHandle.current().children().forEach(child -> existing.add(child.pid()));
        try (var runtime = runtime()) {
            ProcessHandle child = ProcessHandle.current().children()
                .filter(candidate -> !existing.contains(candidate.pid()))
                .filter(candidate -> candidate.info().command().orElse("").contains("Mail.Cli"))
                .findFirst().orElseThrow();
            CountDownLatch entered = new CountDownLatch(1);
            runtime.registerAsyncTool("Echo", args -> { entered.countDown(); return new CompletableFuture<>(); });
            var run = runtime.run(program(), input("x"));
            assertTrue(entered.await(10, TimeUnit.SECONDS));
            child.destroyForcibly();
            var error = assertThrows(ExecutionException.class, () -> run.get(5, TimeUnit.SECONDS));
            assertInstanceOf(MailException.Process.class, error.getCause());
        }
    }

    @Test void explicitPathOverridesConfiguredPath() throws Exception {
        Path file = temp.resolve("CLI with spaces.exe");
        Files.writeString(file, "placeholder");
        try (var located = CliLocator.resolve(file)) {
            assertEquals(file.toAbsolutePath(), located.executable);
        }
        assertTrue(Files.exists(file), "An external CLI must never be deleted by the SDK");
    }
}
