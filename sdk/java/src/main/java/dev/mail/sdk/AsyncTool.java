package dev.mail.sdk;

import com.fasterxml.jackson.databind.JsonNode;
import java.util.concurrent.CompletionStage;

@FunctionalInterface
public interface AsyncTool {
    CompletionStage<JsonNode> execute(JsonNode input) throws Exception;
}
