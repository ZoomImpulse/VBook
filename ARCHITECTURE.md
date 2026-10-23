# Local Audiobook Studio Architecture

## Initial vertical slice

The first deliverable is an offline-capable Windows desktop shell that imports an
EPUB, presents its chapters in reading order, and lets the user inspect and edit
extracted chapter text. Import never extracts files to disk or changes the source
book. The UI remains useful without any AI models.

## Components

- **VBook.App**: Avalonia desktop application (MVVM with CommunityToolkit.Mvvm,
  compiled bindings, resource-based design tokens, dependency injection, and a
  file-picker service abstraction). The initial workspace focuses on import and
  chapter editing.
- **VBook.Core**: UI-independent domain types and EPUB parsing. Provider
  abstractions, project persistence, and narrative memory belong here as they are
  implemented.
- **Python worker**: A separate local process for LLM inference, TTS, and audio
  processing. It will expose a typed local protocol and must not require cloud
  services. Model-specific code stays behind provider interfaces.
- **SQLite and local files**: Project metadata, narrative memory, and resumable
  job state belong in SQLite. Generated audio and caches belong in per-project
  directories.

## Data flow

1. The user selects an EPUB in the desktop application.
2. `VBook.Core` reads the EPUB container, package manifest, and spine directly
   from the ZIP archive and emits chapters in spine order.
3. The UI displays extracted text without changing the original. Project
   metadata and chapter text are saved transactionally to a local SQLite
   project catalog; recent projects can be reopened after restarting.
4. Future chapter analysis packages only a bounded scene and relevant SQLite
   memory for the configured local LLM provider.
5. Validated direction decisions are sent to a TTS provider, with generated
   segments cached and job state persisted for recovery.

## Boundaries and reliability

- EPUB parsing uses an interface so future TXT, HTML, and PDF importers can be
  added without coupling the UI to file formats.
- LLM and TTS providers expose capabilities; the UI must not claim unsupported
  controls are applied.
- Exact source text remains authoritative. Direction output must be validated
  for complete, ordered coverage before audio generation.
- Context budgets are enforced before local model requests.
- Long-running jobs accept cancellation and persist completed segment results.
- No book content, model prompts, or audio is uploaded or sent to telemetry.

## Staged implementation

1. Architecture and EPUB import with chapter-order tests.
2. Avalonia book workspace, editable text, and transactional SQLite project storage.
3. Narrative models and local worker contracts with mock providers.
4. Resumable generation and audio playback/export.
5. Validated local Qwen-compatible LLM and Qwen3-TTS integrations; evaluate
   additional providers only after compatibility is demonstrated.

The solution targets .NET 10 (SDK 10.0.x). The Python worker environment is
Python 3.11.
