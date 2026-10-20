// ============================================================================
// ChatOrchestrator.cs — the "brain" between the browser and the model.
//
// A plain chat is one request to Ollama. With tools it becomes a loop:
//
//   1. Send the conversation plus the list of available tools to the model.
//   2. The model either
//        a) answers with text  -> done, or
//        b) asks to call one or more tools, e.g. read_file("C:\notes.txt").
//   3. For (b): run each tool here in C#, add the results to the conversation
//      as "tool" messages, and go back to step 1, so the model can now answer
//      using the results (or ask for another tool).
//
// Example: "what's in my Documents folder?"
//   round 1: model -> list_directory("C:\Users\cbb\Documents")
//            we run it and add the listing to the conversation
//   round 2: model -> "Your Documents folder contains 3 folders and ..."
//
// Throughout, everything is streamed to the browser as ChatStreamEvents
// (thinking, content, which tool is being run, done).
// ============================================================================

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIOrchestrator.Ollama;
using AIOrchestrator.Tools;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Orchestration;

// IEnumerable<IChatTool> receives every tool registered in Program.cs
// (AddSingleton<IChatTool, ...>), so new tools need no changes here.
public sealed class ChatOrchestrator(
    OllamaClient ollama,
    IEnumerable<IChatTool> tools,
    IOptions<OllamaOptions> options,
    ILogger<ChatOrchestrator> logger)
{
    // Safety limit: stop if the model keeps calling tools without ever answering
    // (small models occasionally get stuck in a loop).
    private const int MaxToolRounds = 10;

    private readonly OllamaOptions _options = options.Value;

    // Tools by name, for looking up which one the model asked for.
    private readonly Dictionary<string, IChatTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    /// <param name="history">The conversation from the browser (user/assistant messages).</param>
    /// <param name="useTools">False = don't offer any tools (the "File access" toggle is off).</param>
    /// <param name="allowWrite">False = don't offer tools that change files (the "Allow writing files" toggle is off).</param>
    public async IAsyncEnumerable<ChatStreamEvent> RunAsync(
        string? model,
        IReadOnlyList<ChatMessage> history,
        bool think,
        bool useTools,
        bool allowWrite,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // --- Build the message list: system prompt first, then the conversation ---
        // The model has no memory between requests. Every request contains the full
        // history so it "remembers" earlier messages in the chat. During tool rounds we
        // keep adding to this list (tool calls + their results).
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
            messages.Add(new ChatMessage("system", _options.SystemPrompt));
        messages.AddRange(history);

        // Choose which tools to offer for this request: none if tools are off, and only the
        // read-only ones if writing is off.
        var offeredTools = useTools
            ? _tools.Values.Where(t => allowWrite || !t.ModifiesFiles).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase)
            : [];

        // Describe the tools to the model in Ollama's format. Null = no tools offered.
        var toolDefinitions = offeredTools.Count > 0
            ? offeredTools.Values
                .Select(t => new OllamaToolDefinition("function", new OllamaFunctionDefinition(t.Name, t.Description, t.Parameters)))
                .ToList()
            : null;

        // Token statistics added up across all rounds, for the "done" event.
        var totalTokens = 0;
        long totalDurationNs = 0;

        // Tool calls that already failed in this request (name + exact arguments). Small models
        // sometimes repeat the same failing call round after round; this stops that early.
        var failedCalls = new HashSet<string>(StringComparer.Ordinal);

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var content = new StringBuilder();
            var toolCalls = new List<OllamaToolCall>();

            // --- Step 1+2: ask the model, forwarding its output to the browser as it streams ---
            await foreach (var chunk in ollama.StreamChatAsync(model, messages, think, toolDefinitions, ct))
            {
                if (chunk.Error is not null)
                {
                    yield return new ChatStreamEvent("error", chunk.Error);
                    yield break;
                }

                if (!string.IsNullOrEmpty(chunk.Message?.Thinking))
                    yield return new ChatStreamEvent("thinking", chunk.Message.Thinking);

                if (!string.IsNullOrEmpty(chunk.Message?.Content))
                {
                    content.Append(chunk.Message.Content);
                    yield return new ChatStreamEvent("content", chunk.Message.Content);
                }

                // Tool calls arrive inside a chunk like any other output. Collect them and run
                // them once the model has finished this round.
                if (chunk.Message?.ToolCalls is { Count: > 0 } calls)
                    toolCalls.AddRange(calls);

                if (chunk.Done)
                {
                    totalTokens += chunk.EvalCount ?? 0;
                    totalDurationNs += chunk.EvalDuration ?? 0;
                }
            }

            // --- (a) No tool calls: the model has given its final answer ---
            if (toolCalls.Count == 0)
            {
                // tokens/second = tokens ÷ seconds (durations are in nanoseconds).
                double? tps = totalTokens > 0 && totalDurationNs > 0
                    ? totalTokens / (totalDurationNs / 1_000_000_000.0)
                    : null;
                yield return new ChatStreamEvent("done", Tokens: totalTokens > 0 ? totalTokens : null, TokensPerSecond: tps);
                yield break;
            }

            // --- (b) Step 3: run the requested tools and add the results to the conversation ---
            // First record the model's own turn (its tool request), so the next round sees
            // what it asked for, followed by the answers.
            messages.Add(new ChatMessage("assistant", content.ToString(), ToolCalls: toolCalls));

            foreach (var call in toolCalls)
            {
                // Tell the browser what's happening, e.g. "read_file: C:\notes.txt".
                yield return new ChatStreamEvent("tool", Describe(call));

                var callKey = $"{call.Function.Name} {call.Function.Arguments.GetRawText()}";
                string result;
                if (failedCalls.Contains(callKey))
                {
                    result = "Error: you already made this exact call and it failed (see the earlier result). Repeating it " +
                             "won't help: change the arguments, try a different approach, or explain the problem to the user.";
                }
                else
                {
                    result = await ExecuteToolAsync(call, offeredTools, ct);
                    if (result.StartsWith("Error:", StringComparison.Ordinal))
                        failedCalls.Add(callKey);
                }
                messages.Add(new ChatMessage("tool", result, ToolName: call.Function.Name));
            }
            // ...then loop back to step 1 with the results included.
        }

        yield return new ChatStreamEvent("error", $"Stopped after {MaxToolRounds} rounds of tool calls without a final answer.");
    }

    /// <summary>
    /// Runs one tool call. Never throws: failures become text the model can read.
    /// Only tools offered in THIS request can run. A model that "remembers" write_file from an
    /// earlier message, or simply makes the call up, can't use it while writing is switched off.
    /// </summary>
    private async Task<string> ExecuteToolAsync(OllamaToolCall call, Dictionary<string, IChatTool> offeredTools, CancellationToken ct)
    {
        var name = call.Function.Name;
        if (!offeredTools.TryGetValue(name, out var tool))
            return _tools.ContainsKey(name)
                ? $"Error: the tool '{name}' is switched off for this conversation. Tell the user to enable it if needed."
                : $"Error: there is no tool named '{name}'. Available tools: {string.Join(", ", offeredTools.Keys)}.";

        try
        {
            var arguments = NormalizeArguments(call.Function.Arguments);
            logger.LogInformation("Running tool {Tool} with {Arguments}", name, arguments.GetRawText());
            return await tool.ExecuteAsync(arguments, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected bug in a tool: log it, and tell the model rather than crashing the chat.
            logger.LogError(ex, "Tool {Tool} failed", name);
            return $"Error: the tool '{name}' failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Ollama normally sends tool arguments as a JSON object, but some models produce
    /// them as a JSON string containing the object ("{\"path\":\"...\"}"). Handle both.
    /// </summary>
    private static JsonElement NormalizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String && arguments.GetString() is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone(); // Clone so it outlives the disposed JsonDocument
        }
        return arguments;
    }

    /// <summary>
    /// A short human-readable label for the UI, e.g. "read_file: C:\notes.txt" or
    /// "search_files: "invoice" in C:\Docs (*.pdf)".
    /// </summary>
    private static string Describe(OllamaToolCall call)
    {
        try
        {
            var args = NormalizeArguments(call.Function.Arguments);
            var path = FileAccessGuard.GetStringArgument(args, "path");
            var query = FileAccessGuard.GetStringArgument(args, "query");
            var pattern = FileAccessGuard.GetStringArgument(args, "pattern");
            var mode = FileAccessGuard.GetStringArgument(args, "mode");

            var label = new StringBuilder(call.Function.Name);
            if (query is not null) label.Append($": \"{query}\" in");
            else if (path is not null) label.Append(':');
            if (path is not null) label.Append($" {path}");
            if (pattern is not null) label.Append($" ({pattern})");
            if (mode is not null) label.Append($" [{mode}]");
            return label.ToString();
        }
        catch (JsonException)
        {
            return call.Function.Name;
        }
    }
}
