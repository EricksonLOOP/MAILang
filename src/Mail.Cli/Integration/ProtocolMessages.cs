using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mail.Cli.Integration;

// ── Inbound (client → server) ─────────────────────────────────────────────────

record HandshakeMsg(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("client")] string? Client);

record LoadMsg(
    [property: JsonPropertyName("load_id")] string LoadId,
    [property: JsonPropertyName("path")] string Path);

record RunMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("input")] JsonElement Input,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("provider_config")] JsonElement? ProviderConfig);

record ToolResponseMsg(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("output")] JsonElement Output);

record ToolErrorMsg(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("error")] string Error);

record CancelMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId);

// ── Outbound (server → client) ────────────────────────────────────────────────

record HandshakeOkMsg(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion);

record HandshakeErrorMsg(
    [property: JsonPropertyName("reason")] string Reason);

record FieldContractDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("required")] bool Required);

record ToolContractDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] FieldContractDto[] Input,
    [property: JsonPropertyName("output")] FieldContractDto[] Output);

record DiagnosticDto(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string Message);

record LoadedMsg(
    [property: JsonPropertyName("load_id")] string LoadId,
    [property: JsonPropertyName("tools")] ToolContractDto[] Tools);

record LoadErrorMsg(
    [property: JsonPropertyName("load_id")] string LoadId,
    [property: JsonPropertyName("diagnostics")] DiagnosticDto[] Diagnostics);

record RunStartedMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId);

record EventDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("operation_id")] string? OperationId,
    [property: JsonPropertyName("call_id")] string? CallId,
    [property: JsonPropertyName("duration_ms")] long? DurationMs);

record EventMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("event")] EventDto Event);

record ToolRequestMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("input")] JsonElement Input);

record ResultMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("output")] JsonElement Output);

record RunErrorMsg(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("error")] string Error);

record ProtocolErrorMsg(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("fatal")] bool Fatal);
