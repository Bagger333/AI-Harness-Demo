namespace AIOrchestrator.Tools;

/// <summary>
/// Settings for the file tools, loaded from the "FileAccess" section of appsettings.json.
/// </summary>
public sealed class FileAccessOptions
{
    public const string SectionName = "FileAccess";

    /// <summary>
    /// The only folders (and their subfolders) the model may list, read, search or write.
    /// An empty list disables file access entirely. Relative paths from the model
    /// are resolved against the first folder in this list.
    /// </summary>
    public string[] AllowedFolders { get; set; } = [];

    /// <summary>
    /// Folder names skipped when reading or searching a folder tree (e.g. bin, obj, node_modules).
    /// They hold generated or downloaded files that would bury the real results.
    /// They can still be listed or read directly by path.
    /// </summary>
    public string[] ExcludedFolderNames { get; set; } = [];

    /// <summary>Files larger than this are refused (protects memory and keeps searches fast).</summary>
    public long MaxFileSizeBytes { get; set; } = 20_000_000;

    /// <summary>
    /// Maximum characters of a single file returned to the model; the rest is cut off.
    /// Roughly 4 characters = 1 token, so 20,000 characters ≈ 5,000 tokens of the context window.
    /// </summary>
    public int MaxCharacters { get; set; } = 20_000;

    /// <summary>
    /// Maximum characters returned by read_files across ALL files combined. Must fit in the
    /// context window (Ollama:ContextLength) along with the conversation.
    /// </summary>
    public int MaxTotalCharacters { get; set; } = 40_000;

    /// <summary>Maximum number of files read_files will read in one call.</summary>
    public int MaxFilesPerRead { get; set; } = 30;

    /// <summary>Maximum number of entries returned when listing a folder.</summary>
    public int MaxDirectoryEntries { get; set; } = 300;

    /// <summary>Maximum number of files search_files looks inside per call.</summary>
    public int MaxFilesToSearch { get; set; } = 2000;

    /// <summary>Maximum number of matching lines search_files returns.</summary>
    public int MaxSearchResults { get; set; } = 60;

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Master switch for the write tools (write_file, edit_file). False = they are never
    /// offered to the model, whatever the UI toggle says.
    /// </summary>
    public bool AllowWrite { get; set; } = true;

    /// <summary>
    /// File types the model may never create or change. These are things Windows can run
    /// (programs, scripts, shortcuts, registry files). A prompt-injected model could otherwise
    /// plant something that executes when double-clicked.
    /// </summary>
    public string[] BlockedWriteExtensions { get; set; } = [];

    /// <summary>Maximum size of the content the model can write in one call.</summary>
    public int MaxWriteCharacters { get; set; } = 200_000;

    /// <summary>
    /// Where copies of files are saved before they're overwritten or edited, relative to
    /// the app folder (or an absolute path). Empty = no backups.
    /// </summary>
    public string BackupFolder { get; set; } = "backups";
}
