package dev.mail.sdk;

import com.fasterxml.jackson.databind.JsonNode;

/** A callback runs off the protocol reader thread. Make shared state thread-safe. */
@FunctionalInterface
public interface Tool {
    JsonNode execute(JsonNode input) throws Exception;
}
