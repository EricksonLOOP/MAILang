package dev.mail.sdk;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Comparator;
import java.util.Locale;
import java.util.zip.ZipInputStream;

/** Resolves an explicit CLI or extracts a trusted CLI distributed inside the JAR. */
final class CliLocator implements AutoCloseable {
    final Path executable;
    private final Path extracted;

    private CliLocator(Path executable, Path extracted) {
        this.executable = executable;
        this.extracted = extracted;
    }

    static CliLocator resolve(Path explicit) {
        if (explicit == null) {
            String configured = System.getProperty("mail.cli.path");
            if (configured == null || configured.isBlank()) configured = System.getenv("MAIL_EXE");
            if (configured != null && !configured.isBlank()) explicit = Path.of(configured);
        }
        if (explicit != null) {
            Path path = explicit.toAbsolutePath().normalize();
            if (!Files.isRegularFile(path)) throw new MailException.Process("MAIL CLI not found: " + path);
            return new CliLocator(path, null);
        }
        String platform = platform();
        String resource = "/dev/mail/sdk/native/" + platform + ".zip";
        var stream = CliLocator.class.getResourceAsStream(resource);
        if (stream == null) throw new MailException.Process(
            "No bundled MAIL CLI for " + platform + ". Use a bundled SDK artifact, " +
            "MailRuntime.start(Path), -Dmail.cli.path, or MAIL_EXE.");
        Path dir = null;
        try (var zip = new ZipInputStream(stream)) {
            dir = Files.createTempDirectory("mail-java-");
            for (var entry = zip.getNextEntry(); entry != null; entry = zip.getNextEntry()) {
                Path target = dir.resolve(entry.getName()).normalize();
                if (!target.startsWith(dir)) throw new IOException("Unsafe CLI ZIP entry");
                if (entry.isDirectory()) Files.createDirectories(target);
                else {
                    Files.createDirectories(target.getParent());
                    Files.copy(zip, target);
                }
            }
            Path executable = dir.resolve(platform.startsWith("win-") ? "Mail.Cli.exe" : "Mail.Cli");
            if (!Files.isRegularFile(executable)) throw new IOException("CLI missing from " + resource);
            if (!platform.startsWith("win-") && !executable.toFile().setExecutable(true, true))
                throw new IOException("Cannot set CLI executable permission");
            return new CliLocator(executable, dir);
        } catch (IOException | RuntimeException error) {
            deleteExtracted(dir);
            throw new MailException.Process("Cannot extract bundled MAIL CLI", error);
        }
    }

    static String platform() {
        String os = System.getProperty("os.name").toLowerCase(Locale.ROOT);
        String arch = System.getProperty("os.arch").toLowerCase(Locale.ROOT);
        String family = os.startsWith("windows") ? "win" : os.contains("mac") ? "osx" : os.contains("linux") ? "linux" : null;
        String cpu = switch (arch) { case "amd64", "x86_64" -> "x64"; case "aarch64", "arm64" -> "arm64"; default -> null; };
        if (family == null || cpu == null) throw new MailException.Process("Unsupported MAIL platform: " + os + "/" + arch);
        return family + "-" + cpu;
    }

    @Override public void close() { deleteExtracted(extracted); }

    private static void deleteExtracted(Path dir) {
        if (dir == null) return;
        try (var paths = Files.walk(dir)) {
            paths.sorted(Comparator.reverseOrder()).forEach(path -> {
                try { Files.deleteIfExists(path); }
                catch (IOException ignored) { path.toFile().deleteOnExit(); }
            });
        } catch (IOException ignored) { /* best effort after subprocess termination */ }
    }
}
