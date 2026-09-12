package dev.mail.sdk;

import com.fasterxml.jackson.databind.ObjectMapper;
import java.nio.file.Path;
import java.util.concurrent.TimeUnit;

/** Run in a fresh JVM against the packaged JAR (not target/classes). */
public final class BundledSmoke {
    public static void main(String[] args) throws Exception {
        try (var runtime = MailRuntime.start()) {
            runtime.registerTool("Echo", input -> input);
            var input = new ObjectMapper().createObjectNode().put("text", "Java bundled CLI ✓");
            var result = runtime.run(Path.of(args[0]), input).get(20, TimeUnit.SECONDS);
            if (!input.equals(result)) throw new AssertionError("Unexpected result: " + result);
            System.out.println(result);
        }
    }
}
