# Local Audiobook Studio

A Windows desktop application for preparing local, multi-character audiobooks.
The current initial milestone is focused on importing EPUB books and inspecting
their chapters. Original EPUB files are read in place and are never modified.

## Current status

- Architecture and component boundaries are documented in
  [ARCHITECTURE.md](./ARCHITECTURE.md).
- EPUB parsing and chapter browsing are the first implementation milestone.
- Local LLM/TTS integrations, SQLite narrative memory, background generation,
  and audio export are staged work and are not represented as available yet.
- No cloud APIs or telemetry are part of the design.

## Development prerequisites

- Windows 11
- .NET 8 SDK (the current development environment)
- Windows App SDK / WinUI 3 dependencies restored through NuGet

## Build

Build the solution from the repository root:

```powershell
dotnet build .\VBook.sln
```

Run the desktop application from Visual Studio or with `dotnet run` for the
`VBook.App` project. Model configuration and generation become available in later
milestones.
