# Local Audiobook Studio

A Windows desktop application for preparing local, multi-character audiobooks.
The current initial milestone is focused on importing EPUB books and inspecting
their chapters. Original EPUB files are read in place and are never modified.

## Current status

- Architecture and component boundaries are documented in
  [ARCHITECTURE.md](./ARCHITECTURE.md).
- Import EPUBs, browse chapters, edit extracted text, and save projects locally
  in SQLite. The latest project reopens when the application starts.
- Local LLM/TTS integrations, SQLite narrative memory, background generation,
  and audio export are staged work and are not represented as available yet.
- No cloud APIs or telemetry are part of the design.

## Development prerequisites

- .NET 10 SDK
- Windows, macOS, or Linux; Avalonia and its dependencies restore through NuGet

The desktop app uses Avalonia UI (Fluent theme, light/dark follows the system)
and needs no separate runtime installer. Shortcuts: **Ctrl+O** imports an EPUB,
**Ctrl+S** saves. Project metadata and chapter text are stored locally in
`%LOCALAPPDATA%\LocalAudiobookStudio\projects.sqlite3`; EPUB source files are
read in place and never modified.

Chapter edits are automatically saved shortly after editing. Use **Save**
to persist immediately, or choose a project in **Recent projects** and select
**Open** to switch back to it.

## Build

Build the solution from the repository root:

```powershell
dotnet build .\VBook.sln
```

Run the desktop application from Visual Studio or with `dotnet run` for the
`VBook.App` project. Model configuration and generation become available in later
milestones.
