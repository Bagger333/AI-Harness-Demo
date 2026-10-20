// ============================================================================
// OllamaClient.cs — the only code that talks to Ollama over HTTP.
//
// It is deliberately simple: send one request, stream back the raw chunks.
// Deciding what to do with them (forwarding text, running tools, looping)
// is ChatOrchestrator's job. To support another AI backend later (OpenAI,
// Azure, ...), you'd write a similar class.
//
// Ollama's HTTP API docs: https://github.com/ollama/ollama/blob/main/docs/api.md
// ============================================================================

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AIOrchestrator.Ollama;

// "Primary constructor" syntax: the parameters (http, options) are available
// throughout the class. Both are supplied by dependency injection (see Program.cs):
//   http    — an HttpClient already pointed at Ollama's BaseUrl
//   options — the "Ollama" settings from appsettings.json
public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options)
{
    private readonly OllamaOptions _options = options.Value;

    /// <summary>
    /// Asks Ollama which models are installed (GET /api/tags, same as `ollama list`).
    /// </summary>
    public async Task<IReadOnlyList<OllamaModelInfo>> GetModelsAsync(CancellationToken ct)
    {
        // GetFromJsonAsync sends the GET and deserializes the JSON response in one step.
        var tags = await http.GetFromJsonAsync<OllamaTagsResponse>("api/tags", ct);
        return tags?.Models ?? []; // [] = an empty list if the response was empty
    }

    /// <summary>
    /// Sends one chat request to Ollama and returns its response chunks as they arrive.
    ///
    /// The return type IAsyncEnumerable plus "yield return" makes this a streaming method:
    /// the caller loops over it with "await foreach", and each "yield return" below hands
    /// one chunk to that loop right away, without waiting for the whole reply.
    ///
    /// Marked internal because OllamaChatChunk is an internal type.
    /// </summary>
    /// <param name="model">Model name, or null/empty to use DefaultModel.</param>
    /// <param name="messages">The complete message list to send, including the system prompt.</param>
    /// <param name="think">Whether a reasoning model should think before answering.</param>
    /// <param name="tools">Tools the model may call, or null for none.</param>
    /// <param name="ct">
    /// Cancelled when the user clicks Stop. [EnumeratorCancellation] makes the token work
    /// correctly with "await foreach".
    /// </param>
    internal async IAsyncEnumerable<OllamaChatChunk> StreamChatAsync(
        string? model,
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<OllamaToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new OllamaChatRequest(
            string.IsNullOrWhiteSpace(model) ? _options.DefaultModel : model,
            messages,
            Stream: true,
            Think: think,
            Tools: tools,
            Options: new OllamaRequestOptions(_options.ContextLength));

        // --- Send the request ---
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(request) // serializes the request to JSON
        };

        // ResponseHeadersRead is what makes streaming work: SendAsync returns as soon as
        // Ollama starts responding, instead of waiting until the whole reply has arrived
        // (the default behaviour).
        using var response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            // e.g. 404 if the model isn't installed. Pass Ollama's message on as an error chunk.
            var body = await response.Content.ReadAsStringAsync(ct);
            yield return new OllamaChatChunk(null, Done: true, null, null,
                Error: $"Ollama returned {(int)response.StatusCode}: {body}");
            yield break; // "yield break" = end the stream
        }

        // --- Read the response one line at a time as it arrives ---
        // Ollama streams NDJSON: each line is a complete JSON object (see OllamaChatChunk).
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // ReadLineAsync waits until a full line has arrived. It returns null when Ollama
        // closes the stream, which ends the loop. ("is { } line" = "is not null, call it line".)
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
            if (chunk is null)
                continue;

            yield return chunk;

            if (chunk.Done || chunk.Error is not null)
                yield break;
        }
    }
}
