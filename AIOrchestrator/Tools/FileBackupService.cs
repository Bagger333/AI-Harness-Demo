using Microsoft.Extensions.Options;

namespace AIOrchestrator.Tools;

/// <summary>
/// Saves a copy of a file before the write tools change it, so any change the model makes
/// can be undone by copying the backup back.
///
/// Backups go to FileAccess:BackupFolder (default: the "backups" folder next to the app),
/// named with a timestamp so nothing is ever overwritten, e.g.
///   backups\20261002-153012-481_notes.txt
/// Old backups are never deleted automatically. Clear the folder yourself now and then.
/// </summary>
public sealed class FileBackupService
{
    private readonly string? _folder;

    // IWebHostEnvironment.ContentRootPath = the app's folder (the project folder when using dotnet run).
    public FileBackupService(IOptions<FileAccessOptions> options, IWebHostEnvironment environment)
    {
        var configured = options.Value.BackupFolder;
        _folder = string.IsNullOrWhiteSpace(configured)
            ? null
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured)); // Combine keeps an absolute path as-is
    }

    /// <summary>
    /// Copies <paramref name="fullPath"/> into the backup folder and returns the copy's path,
    /// or null if backups are turned off or the file doesn't exist (nothing to back up).
    /// </summary>
    public string? Backup(string fullPath)
    {
        if (_folder is null || !File.Exists(fullPath))
            return null;

        Directory.CreateDirectory(_folder);
        var destination = Path.Combine(_folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{Path.GetFileName(fullPath)}");
        File.Copy(fullPath, destination, overwrite: false);
        return destination;
    }
}
