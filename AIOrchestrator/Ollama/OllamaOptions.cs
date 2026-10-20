namespace AIOrchestrator.Ollama;

/// <summary>
/// Settings for connecting to Ollama, loaded from the "Ollama" section of appsettings.json
/// (wired up in Program.cs with <c>builder.Services.Configure&lt;OllamaOptions&gt;</c>).
///
/// The values below are fallbacks used only if appsettings.json doesn't set them.
/// Each one can also be overridden with an environment variable using a double underscore,
/// e.g. <c>Ollama__BaseUrl=http://other-pc:11434</c>, which is handy when deploying.
/// </summary>
public sealed class OllamaOptions
{
    /// <summary>Name of the section in appsettings.json.</summary>
    public const string SectionName = "Ollama";

    /// <summary>Address of the Ollama server. 11434 is Ollama's default port.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Model preselected in the UI, and used when the browser doesn't specify one.</summary>
    public string DefaultModel { get; set; } = "qwen3:8b";

    /// <summary>
    /// Instructions added as the first message of every conversation, telling the model
    /// how to behave. The user never sees it in the chat. Leave it empty to skip it.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// How long to wait for Ollama to start answering a request before giving up. Ollama only
    /// answers once it has read the whole conversation, which on a CPU-only machine can take many
    /// minutes when files have been read. 0 = no limit (the Stop button still cancels).
    /// </summary>
    public int RequestTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Context window in tokens (Ollama's "num_ctx"): how much text the model can consider at
    /// once, including the whole conversation and any files it has read. Ollama defaults to
    /// 4096, which one medium file fills. Higher values use more RAM: for qwen3:8b,
    /// 16384 adds about 2.5 GB.
    /// </summary>
    public int ContextLength { get; set; } = 16384;
}
