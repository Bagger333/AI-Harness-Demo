using System.Text.Json;

namespace AIOrchestrator.Tools;

/// <summary>
/// A function the model is allowed to call. To give the model a new ability, write a
/// class implementing this interface and register it in Program.cs with
/// <c>builder.Services.AddSingleton&lt;IChatTool, YourTool&gt;()</c>. ChatOrchestrator
/// picks up every registered tool automatically.
/// </summary>
public interface IChatTool
{
    /// <summary>The function name the model uses to call it, e.g. "read_file".</summary>
    string Name { get; }

    /// <summary>
    /// Plain-English explanation for the model of what the tool does and when to use it.
    /// The model decides whether to call a tool from this description alone, so be specific.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The tool's parameters as a JSON Schema object, e.g.
    /// { type = "object", properties = new { path = new { type = "string" } }, required = ["path"] }.
    /// </summary>
    object Parameters { get; }

    /// <summary>
    /// True for tools that change things on disk (write_file, edit_file). These are only
    /// offered to the model when the user has "Allow writing files" switched on.
    /// "=> false" is a default implementation, so read-only tools don't need to mention it.
    /// </summary>
    bool ModifiesFiles => false;

    /// <summary>
    /// Runs the tool. <paramref name="arguments"/> is the JSON object the model supplied,
    /// e.g. {"path":"C:\\Users\\cbb\\notes.txt"}. The returned text is passed back to the
    /// model as the tool's result. Return problems as a text message (e.g. "Error: file not
    /// found") rather than throwing, so the model can react, e.g. by trying another path.
    /// </summary>
    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken ct);
}
