// ============================================================================
// Program.cs — the entry point of the web app.
//
// In modern ASP.NET Core there is no Main() method or Startup class: this file
// IS the program ("top-level statements"). It runs top to bottom and does three
// things:
//   1. Registers services (configuration + the Ollama HTTP client).
//   2. Sets up the request pipeline (serve the chat page from wwwroot/).
//   3. Defines the API endpoints the browser calls (/api/...).
//
// Overall flow of a chat message:
//   Browser (wwwroot/app.js)
//     --POST /api/chat-->  ChatOrchestrator  --POST /api/chat-->  Ollama (localhost:11434)
//     <--streamed lines--   (runs tools)     <--streamed lines--
// ============================================================================

using System.Text.Json;
using AIOrchestrator.Ollama;
using AIOrchestrator.Orchestration;
using AIOrchestrator.Tools;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 1. SERVICES (dependency injection)
// ---------------------------------------------------------------------------

// Bind the "Ollama" section of appsettings.json to the OllamaOptions class.
// Any class can then ask for IOptions<OllamaOptions> to read those settings.
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));

// Register OllamaClient as a "typed HttpClient". ASP.NET Core creates the
// HttpClient for us (and manages connection pooling), configures it with the
// lambda below, and passes it into OllamaClient's constructor. Any endpoint that
// lists OllamaClient as a parameter gets one injected automatically.
builder.Services.AddHttpClient<OllamaClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;

    // All requests are relative to this, e.g. "api/chat" -> http://localhost:11434/api/chat.
    // The trailing slash matters: without it, relative paths would replace the last segment.
    http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

    // HttpClient defaults to a 100-second timeout. On a CPU-only machine Ollama can need many
    // minutes just to read a long conversation before the first word comes back, so allow longer
    // (configurable in appsettings.json; 0 = no limit, the Stop button still cancels).
    http.Timeout = options.RequestTimeoutMinutes > 0
        ? TimeSpan.FromMinutes(options.RequestTimeoutMinutes)
        : Timeout.InfiniteTimeSpan;
});

// --- Tools the model can call ---
// Settings for which folders may be accessed (the "FileAccess" section of appsettings.json).
builder.Services.Configure<FileAccessOptions>(builder.Configuration.GetSection(FileAccessOptions.SectionName));
// The security check both file tools use. Singleton = one shared instance for the whole app.
builder.Services.AddSingleton<FileAccessGuard>();
// Each tool is registered as an IChatTool. ChatOrchestrator receives all of them, so adding
// a new tool is just: write a class implementing IChatTool, add one line here.
builder.Services.AddSingleton<IChatTool, ListDirectoryTool>();
builder.Services.AddSingleton<IChatTool, ReadFileTool>();
builder.Services.AddSingleton<IChatTool, ReadFilesTool>();
builder.Services.AddSingleton<IChatTool, SearchFilesTool>();
// Write tools (ModifiesFiles = true): only offered when writing is allowed.
// FileBackupService saves a copy of a file before either of them changes it.
builder.Services.AddSingleton<FileBackupService>();
builder.Services.AddSingleton<IChatTool, WriteFileTool>();
builder.Services.AddSingleton<IChatTool, EditFileTool>();

// Runs the chat + tool-calling loop. Transient = a new instance per request
// (it depends on OllamaClient, which the HttpClient factory also creates per use).
builder.Services.AddTransient<ChatOrchestrator>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// 2. PIPELINE — serve the frontend
// ---------------------------------------------------------------------------

// UseDefaultFiles: a request for "/" is rewritten to "/index.html".
// UseStaticFiles: files in wwwroot/ (index.html, app.js, styles.css) are served as-is.
// Order matters — DefaultFiles must come before StaticFiles.
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------------------
// 3. API ENDPOINTS ("minimal APIs")
//
// Each MapGet/MapPost connects a URL to a lambda. Lambda parameters are filled
// in automatically: services (OllamaClient, IOptions) come from dependency
// injection, a record type (ChatRequest) is parsed from the JSON body, and
// CancellationToken / HttpContext come from the current request.
// ---------------------------------------------------------------------------

// Group all endpoints under the "/api" prefix.
var api = app.MapGroup("/api");

// GET /api/config — tells the frontend which model to preselect in the dropdown,
// which folders the file tools can access (shown under the "File access" toggle), and whether
// writing is enabled on the server (if not, the "Allow writing files" toggle is hidden).
api.MapGet("/config", (IOptions<OllamaOptions> options, IOptions<FileAccessOptions> fileOptions, FileAccessGuard fileAccess) =>
    Results.Ok(new
    {
        defaultModel = options.Value.DefaultModel,
        allowedFolders = fileAccess.Roots,
        allowWrite = fileOptions.Value.AllowWrite
    }));

// GET /api/models — lists the models installed in Ollama (fills the model dropdown).
api.MapGet("/models", async (OllamaClient ollama, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await ollama.GetModelsAsync(ct));
    }
    catch (HttpRequestException ex)
    {
        // Ollama isn't running or is unreachable. Return 502 Bad Gateway
        // ("the server behind me failed") so the UI can show a warning.
        return Results.Problem($"Could not reach Ollama: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

// POST /api/chat — the main endpoint. Receives the conversation so far and
// streams the model's reply back as it's generated. ChatOrchestrator does the
// work (including running any tools the model asks for); this endpoint only
// writes its events to the response.
//
// Streaming format: NDJSON (newline-delimited JSON). Each line is one small JSON
// object (a ChatStreamEvent), for example:
//   {"type":"thinking","text":"Let me"}
//   {"type":"tool","text":"read_file: C:\\notes.txt"}
//   {"type":"content","text":"Hello"}
//   {"type":"done","tokens":42,"tokensPerSecond":12.3}
// The browser reads these lines one at a time and updates the page live.
//
// This endpoint writes directly to the response body instead of returning a
// result, which lets us send data bit by bit rather than all at once at the end.
api.MapPost("/chat", async (ChatRequest request, ChatOrchestrator orchestrator, HttpContext http, ILogger<Program> logger) =>
{
    // Reject empty requests.
    if (request.Messages is not { Count: > 0 })
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // RequestAborted fires when the browser disconnects, e.g. when the user clicks
    // "Stop" (app.js aborts the fetch). We pass this token all the way down to the
    // Ollama request, so stopping in the UI actually stops generation in Ollama
    // instead of letting it run on in the background.
    var ct = http.RequestAborted;

    http.Response.ContentType = "application/x-ndjson";
    http.Response.Headers.CacheControl = "no-cache";

    // Helper: write one event as a JSON line and flush immediately.
    // Without the flush, ASP.NET Core would buffer the output and the user would
    // see the reply arrive in large chunks instead of word by word.
    async Task WriteEvent(ChatStreamEvent evt)
    {
        await JsonSerializer.SerializeAsync(http.Response.Body, evt, cancellationToken: ct);
        await http.Response.Body.WriteAsync("\n"u8.ToArray(), ct); // "\n"u8 = the newline as UTF-8 bytes
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        // RunAsync is an IAsyncEnumerable: each loop iteration receives the next event
        // (a piece of text, a tool being run, ...) as soon as it happens, and we forward
        // it straight to the browser.
        await foreach (var evt in orchestrator.RunAsync(request.Model, request.Messages, request.Think, request.UseTools, request.AllowWrite, ct))
            await WriteEvent(evt);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // The user clicked Stop / closed the tab. This is expected, not an error.
    }
    catch (Exception ex)
    {
        // Anything else. If it escaped, ASP.NET Core would cut the response off and the browser
        // would only say "network error", so log it and send it as an event the UI can show.
        logger.LogError(ex, "Chat request failed");
        var message = ex switch
        {
            HttpRequestException => $"Could not reach Ollama: {ex.Message}",
            TaskCanceledException => "Ollama took too long to respond. Try again, or raise Ollama:RequestTimeoutMinutes in appsettings.json.",
            IOException => $"The connection to Ollama was lost mid-reply ({ex.Message}). Ollama may have crashed or run out of memory; check its log.",
            _ => $"Unexpected server error: {ex.Message}"
        };
        try
        {
            await WriteEvent(new ChatStreamEvent("error", message));
        }
        catch (Exception writeEx) when (writeEx is IOException or OperationCanceledException)
        {
            // The browser is already gone, so there's no one to tell.
        }
    }
});

// Start the web server and block until it shuts down (Ctrl+C).
app.Run();

// The JSON body the browser sends to POST /api/chat.
//   Model    — which Ollama model to use (null = use DefaultModel from settings)
//   Messages — the whole conversation so far. The model has no memory between
//              requests, so the full history is sent every time.
//   Think    — whether qwen3 should "think" (reason) before answering.
//   UseTools   — whether the model may use the file tools ("File access" toggle).
//   AllowWrite — whether it may also create/change files ("Allow writing files" toggle).
//                Defaults to false, so a client must explicitly ask for write access.
record ChatRequest(string? Model, List<ChatMessage> Messages, bool Think = true, bool UseTools = true, bool AllowWrite = false);
