package dev.mail.sdk;

import com.fasterxml.jackson.databind.JsonNode;

/** Failures reported by the SDK or the MAIL subprocess. */
public class MailException extends RuntimeException {
    public MailException(String message) { super(message); }
    public MailException(String message, Throwable cause) { super(message, cause); }

    public static final class Process extends MailException {
        public Process(String message) { super(message); }
        public Process(String message, Throwable cause) { super(message, cause); }
    }
    public static final class Protocol extends MailException {
        public Protocol(String message) { super(message); }
    }
    public static final class Load extends MailException {
        private final JsonNode diagnostics;
        public Load(JsonNode diagnostics) {
            super("MAIL compilation failed: " + diagnostics);
            this.diagnostics = diagnostics.deepCopy();
        }
        public JsonNode diagnostics() { return diagnostics.deepCopy(); }
    }
    public static final class Run extends MailException {
        public Run(String message) { super(message); }
    }
    public static final class Tool extends MailException {
        public Tool(String message) { super(message); }
    }
}
