# AI Orchestrator

ASP.NET Core (.NET 10) chat UI for a local Ollama model.

```
Browser (wwwroot/)  ──POST /api/chat──▶  C# server  ──/api/chat──▶  Ollama (localhost:11434)
                    ◀── NDJSON stream ──             ◀── NDJSON ──
```

## Run

```powershell
dotnet run --project AIOrchestrator --launch-profile http
```

Open http://localhost:5135.

## Configuration (`AIOrchestrator/appsettings.json`)

| Key | Purpose |
| --- | --- |
| `Ollama:BaseUrl` | Where Ollama is listening |
| `Ollama:DefaultModel` | Model preselected in the UI |
| `Ollama:SystemPrompt` | Prepended to every conversation |
| `Ollama:RequestTimeoutMinutes` | Max time for a single generation |
| `Ollama:ContextLength` | Context window in tokens (`num_ctx`); room for conversation + file contents |
| `FileAccess:AllowedFolders` | Folders the file tools may list/read (subfolders included). Empty = no file access |
| `FileAccess:MaxFileSizeBytes` | Larger files are refused |
| `FileAccess:MaxCharacters` | File text beyond this is truncated before it goes to the model |
| `FileAccess:MaxDirectoryEntries` | Max entries returned from a folder listing |
| `FileAccess:AllowWrite` | Master switch for the write tools |
| `FileAccess:BlockedWriteExtensions` | File types that may never be written |
| `FileAccess:BackupFolder` | Where backups go before a file is changed (empty = no backups) |

## Tools

| Tool | What it does |
| --- | --- |
| `list_directory` | Lists subfolders and files in a folder |
| `read_file` | Reads one file: text files plus Word (.docx), Excel (.xlsx), PowerPoint (.pptx) and PDF |
| `read_files` | Reads all files in a folder (optional pattern like `*.md`), within a total size budget |
| `search_files` | Finds a phrase inside files in a folder tree; returns file + line number for each match |
| `write_file` ✏️ | Creates a text file (`create`, default), or replaces (`overwrite`) / adds to (`append`) an existing one |
| `edit_file` ✏️ | Replaces an exact piece of text in an existing file (must match once, or `replace_all`) |

✏️ = write tools. They are only offered when **Allow writing files** is on in the UI and
`FileAccess:AllowWrite` is `true`. Extra guardrails on top of `AllowedFolders`:

- No writing inside hidden/system folders or folders starting with `.` (e.g. `AppData`, `.ssh`, `.git`)
- No writing file types listed in `FileAccess:BlockedWriteExtensions` (`.exe`, `.bat`, `.ps1`, ...)
- No writing Word/Excel/PowerPoint/PDF (plain text only)
- Before an existing file is changed, a copy is saved to `FileAccess:BackupFolder`
  (default `AIOrchestrator/backups/`, named `yyyyMMdd-HHmmss-fff_<file>`). Copy it back to undo.

Document text extraction lives in `Tools/FileTextExtractor.cs` (DocumentFormat.OpenXml for Office, PdfPig for PDF).
Not supported: old `.doc/.xls/.ppt` files, images, and scanned PDFs (those would need OCR).

`Orchestration/ChatOrchestrator.cs`
runs the loop: model asks for a tool → C# runs it → result goes back to the model → repeat until it answers.
`FileAccessGuard` blocks any path outside `AllowedFolders` (including `..\` tricks).

To add a tool: implement `IChatTool` and register it in `Program.cs` with
`builder.Services.AddSingleton<IChatTool, YourTool>();`.

Any of these can be overridden with environment variables, e.g. `Ollama__BaseUrl=http://gpu-box:11434`.

## API

- `GET /api/models` – models installed in Ollama
- `GET /api/config` – default model
- `POST /api/chat` – body `{ model?, think, messages: [{role, content}] }`, streams one JSON event per line:
  `{"type":"thinking"|"content"|"done"|"error", "text"?, "tokens"?, "tokensPerSecond"?}`

## Before hosting publicly

- Add authentication (anyone who reaches the site can use your model).
- Add rate limiting (`builder.Services.AddRateLimiter`).
- Keep Ollama bound to localhost / a private network — only this app should talk to it.
- Chat history is currently stored in the browser's localStorage; move it server-side if you need accounts.
