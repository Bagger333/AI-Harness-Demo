// ============================================================================
// OllamaModels.cs — the data shapes (DTOs) that get converted to/from JSON.
//
// "record" is a compact C# class for plain data: the constructor parameters
// become read-only properties. [JsonPropertyName("...")] sets the exact JSON
// field name, because Ollama uses lowercase/snake_case names ("eval_count")
// while C# uses PascalCase (EvalCount).
// [JsonIgnore(Condition = WhenWritingNull)] leaves a field out of the JSON
// entirely when it's null, instead of sending "field": null.
//
// "internal" types are used only inside this app (talking to Ollama).
// "public" types are also exchanged with the browser or used by public classes.
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIOrchestrator.Ollama;

/// <summary>
/// One message in a conversation. Role is one of:
///   "system"    — instructions for the model (the SystemPrompt)
///   "user"      — you
///   "assistant" — the model; may contain ToolCalls instead of (or as well as) text
///   "tool"      — the result of running a tool; ToolName says which one
/// The browser only ever sends user/assistant messages with Role + Content.
/// The tool fields are used by ChatOrchestrator during the tool-calling loop.
/// </summary>
public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OllamaToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolName = null);

/// <summary>
/// A request from the model to run a tool, e.g.
///   {"function":{"name":"read_file","arguments":{"path":"C:\\notes.txt"}}}
/// </summary>
public sealed record OllamaToolCall(
    [property: JsonPropertyName("function")] OllamaToolCallFunction Function);

public sealed record OllamaToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    // Kept as raw JSON (JsonElement) because every tool has different arguments.
    // Each tool reads the fields it needs.
    [property: JsonPropertyName("arguments")] JsonElement Arguments);

/// <summary>
/// Describes one available tool to the model (sent in the request's "tools" list):
///   {"type":"function","function":{"name":"read_file","description":"...","parameters":{JSON Schema}}}
/// </summary>
internal sealed record OllamaToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OllamaFunctionDefinition Function);

internal sealed record OllamaFunctionDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    // Declared as object so each tool can supply its schema as an anonymous object.
    [property: JsonPropertyName("parameters")] object Parameters);

/// <summary>
/// Request body sent to Ollama's POST /api/chat.
/// Serialized as e.g. {"model":"qwen3:8b","messages":[...],"stream":true,"think":true,"tools":[...]}.
/// </summary>
internal sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    // true = Ollama sends the reply piece by piece instead of all at once at the end.
    [property: JsonPropertyName("stream")] bool Stream,
    // true = reasoning models (like qwen3) think first, sent separately as "thinking".
    [property: JsonPropertyName("think")] bool Think,
    // The tools the model may call. Null = no tools (the model just answers).
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OllamaToolDefinition>? Tools,
    [property: JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OllamaRequestOptions? Options);

/// <summary>Model runtime settings sent with each request.</summary>
internal sealed record OllamaRequestOptions(
    // Context window size in tokens: how much conversation (including file contents)
    // the model can "see" at once. Ollama's default is small (4096), and anything over
    // it is silently dropped from the start of the conversation.
    [property: JsonPropertyName("num_ctx")] int NumCtx);

/// <summary>
/// One line of Ollama's streamed /api/chat response. Ollama sends many of these,
/// each holding a few characters of the reply, for example:
///   {"message":{"role":"assistant","content":"","thinking":"Okay"},"done":false}
///   {"message":{"role":"assistant","content":"Hello"},"done":false}
///   {"message":{"role":"assistant","content":"","tool_calls":[...]},"done":false}
///   {"message":{...},"done":true,"eval_count":42,"eval_duration":3500000000}
/// Fields that aren't present in a given line are left null.
/// </summary>
internal sealed record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaChunkMessage? Message,
    // true on the very last line.
    [property: JsonPropertyName("done")] bool Done,
    // Final line only: number of tokens generated...
    [property: JsonPropertyName("eval_count")] int? EvalCount,
    // ...and how long that took, in nanoseconds. Used to compute tokens/second.
    [property: JsonPropertyName("eval_duration")] long? EvalDuration,
    // Present only if something went wrong (e.g. the model name doesn't exist).
    [property: JsonPropertyName("error")] string? Error);

/// <summary>The contents of one chunk: part of the answer, part of the thinking, and/or tool calls.</summary>
internal sealed record OllamaChunkMessage(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("thinking")] string? Thinking,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<OllamaToolCall>? ToolCalls);

/// <summary>Response of Ollama's GET /api/tags, which lists installed models.</summary>
internal sealed record OllamaTagsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<OllamaModelInfo> Models);

/// <summary>
/// One installed model. Ollama returns more fields than this, but JSON fields
/// without a matching property are simply ignored.
/// </summary>
public sealed record OllamaModelInfo(
    [property: JsonPropertyName("name")] string Name,
    // Size on disk, in bytes.
    [property: JsonPropertyName("size")] long Size,
    // e.g. ["completion","tools","thinking"].
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities);

/// <summary>
/// One event the server streams to the browser (one JSON line each).
/// This is our own simplified format, so the browser doesn't need to know Ollama's format.
///   Type  = "thinking" | "content" | "tool" | "done" | "error"
///   Text  = the new piece of text (thinking/content), a description of the tool
///           being run (tool, e.g. "read_file: C:\notes.txt"), or the error message
///   Tokens / TokensPerSecond = statistics, sent only with "done"
/// The "= null" defaults mean only Type is required when creating one.
/// </summary>
public sealed record ChatStreamEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("tokens")] int? Tokens = null,
    [property: JsonPropertyName("tokensPerSecond")] double? TokensPerSecond = null);
