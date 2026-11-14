# Local Audiobook Studio Architecture

## Initial vertical slice

The first deliverable is an offline-capable Windows desktop shell that imports an
EPUB, presents its chapters in reading order, and lets the user inspect and edit
extracted chapter text. Import never extracts files to disk or changes the source
book. The UI remains useful without any AI models.

## Components

- **VBook.App**: Avalonia desktop application (MVVM with CommunityToolkit.Mvvm,
  compiled bindings, resource-based light/dark design tokens, dependency
  injection, and file-picker/theme preference service abstractions). The
  workspace presents persistent project/chapter context and a guided,
  directly navigable Prepare/Structure/Produce/Publish workflow. The production
  queue derives chapter readiness from saved narrative analyses and generation
  jobs, while transitions provide restrained interaction feedback.
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
   Extracted headings and list items retain reading order, tables are rendered
   as spoken row summaries, code blocks become omission notices, and explicit
   line breaks are preserved.
3. The UI displays speech-prepared text without changing the original EPUB. Project
   metadata and chapter text are saved transactionally to a local SQLite
   project catalog; recent projects can be reopened after restarting.
4. Structure analysis sends bounded chapter excerpts to a configured local LLM;
   its genre and role suggestions stay in a review state until explicitly applied.
5. Each bounded chapter chunk is sent once to the local LLM for a combined
   narrative and spoken-wording analysis. Dialogue and edit spans are validated
   against the same original chunk; edit suggestions are stored separately and
   applied only to the corresponding narration segment when accepted.
6. Validated direction decisions and accepted spoken-wording edits are sent to a
   TTS provider, with generated segments cached and job state persisted for
   recovery. Qwen3-TTS is the default local provider and uses pinned CUDA
   runtime, VoiceDesign, and Base models. Voice profiles are stored per project,
   provider, and speaker; Qwen profiles persist their voice description, spoken
   language, generated reference WAV, and exact reference text. The VoiceDesign
   model creates the staged reference; the Base model uses it for consistent
   clone synthesis. Kokoro is an explicitly downloaded sherpa-onnx CPU
   alternative, and Piper can be selected when its executable and voice files
   are configured. Other providers receive native-language recommendations only
   when verified metadata matches the effective spoken language. Proposals are
   applied per speaker, and written directions remain in the provider's
   instruction language. Mock tone synthesis is limited to tests and development
   code.

## Boundaries and reliability

- EPUB parsing uses an interface so future TXT, HTML, and PDF importers can be
  added without coupling the UI to file formats.
- LLM and TTS providers expose capabilities; the UI must not claim unsupported
  controls are applied.
- Exact source text remains authoritative. Direction output must be validated
  for complete, ordered coverage before audio generation.
- EPUB block structure is converted to speech-friendly chapter text during
  import. Single-narrator segmentation keeps paragraph and explicit line
  boundaries, including verse, and splits long lines at sentence boundaries.
- Context budgets are enforced before local model requests.
- Long-running jobs accept cancellation and persist completed segment results.
- No book content, model prompts, or audio is uploaded or sent to telemetry.
- The app defaults to Ollama's OpenAI-compatible endpoint at
  `http://localhost:11434/v1/chat/completions` with
  `frob/qwen3.5-instruct:9b`. The endpoint and model can be overridden with
  `VBOOK_LLM_ENDPOINT` and `VBOOK_LLM_MODEL`.
- Theme appearance follows the operating system by default. A user-selected
  System, Light, or Dark override is stored locally in
  `%LOCALAPPDATA%\LocalAudiobookStudio\settings.json`.
- LLM requests use deterministic temperature settings and JSON response mode.
  Provider output is treated as untrusted: structure suggestions require user
  review; combined narrative dialogue and speech-edit spans must match exact
  source passages. Speech edits are individually accepted or rejected, and
  accepted edits never modify imported chapter text.

## Narrative analysis and the worker contract (stage 3)

- `VBook.Core.Narrative` models a chapter as ordered `NarrativeSegment`s
  (narration or attributed dialogue) that store only offsets into the chapter
  text, so the source text stays authoritative. `AnalysisValidator` rejects gaps,
  overlaps, empty segments, and unknown speakers; `ChapterAnalysis.SourceHash`
  marks an analysis stale when the chapter text is edited afterwards.
- `ILlmProvider` and `ITtsProvider` expose `ProviderCapabilities` (name, whether
  it is a mock, context budget). `ChapterAnalyzer` enforces the budget by chunking
  at line breaks, carries known characters forward between chunks, merges the
  results, and validates the whole. `MockLlmProvider` (quote-based heuristics)
  and `MockTtsProvider` (tone WAVs) run the pipeline without AI/TTS models.
- `WorkerProtocol` defines typed JSON messages with a `type` discriminator
  (`analyzeChapter`, `cancel` / `progress`, `analysisCompleted`, `failed`,
  `cancelled`). The app talks to `INarrativeWorker`; `ProtocolNarrativeWorker`
  speaks the protocol over an `IWorkerTransport`. `LocalWorkerHost` is the
  in-process transport today; the Python worker will be a process transport that
  frames the same messages as JSON lines and sends `cancel` when the token fires.
- `INarrativeStore` (implemented by `SqliteProjectStore`) persists project-wide
  characters, per-chapter analyses, segments, and speech-edit review decisions
  in the same SQLite file.
- `ChapterAnalyzer` requests dialogue attribution and optional speech edits in
  one model call per bounded chunk. It validates both against the source and
  rejects edit spans that cross narration/dialogue boundaries.
- `SpeechScriptBuilder` applies only accepted, non-overlapping edits whose exact
  source fingerprint still matches. Accepted text is substituted within the
  original narration segment for generation; the chapter text and dialogue
  attribution offsets remain unchanged.
- `NarrationMode` is stored on the project and on each analysis, so changing a
  project's single-narrator/multi-voice setting invalidates an analysis made in
  the other mode. TTS voice profiles are stored per project, provider, and
  speaker, so provider-specific voices and Qwen language/style/reference data
  remain independent. Existing voice-profile rows migrate with null reference
  fields; non-Qwen profiles and older Qwen profiles remain readable.

## Audio generation (stage 4)

- `AudioGenerationService` validates the source fingerprint and narration mode,
  then synthesizes analysis segments through `ITtsProvider`. Provider-specific
  voice, language, and instruction settings are included in cache fingerprints,
  so changing a profile invalidates only the relevant audio. `QwenTtsProvider`
  sends requests through a persistent local worker using CUDA and validates its
  WAV output. `KokoroTtsProvider` uses sherpa-onnx offline inference and
  converts generated samples to validated PCM WAV; `PiperTtsProvider` invokes
  the configured local Piper CLI and validates its WAV output.
- Qwen installation is opt-in. The app creates an isolated Python 3.11–3.13
  environment, installs pinned CUDA-enabled PyTorch/Qwen packages, verifies
  CUDA, and downloads pinned Qwen3-TTS 1.7B VoiceDesign and Base models with
  file-size and hash checks. The worker loads one model at a time, switches from
  VoiceDesign reference generation to Base voice cloning, and caches clone
  prompts only for the lifetime of the local worker. Qwen inference is offline
  after setup and requires an NVIDIA CUDA GPU.
- Kokoro model installation is opt-in. The app downloads a pinned sherpa-onnx
  release archive, checks the published SHA-256 digest, safely extracts the
  required model files into local application data, and only then activates the
  provider. No network access is needed for inference.
- Generation jobs and each completed WAV segment are persisted in SQLite.
  Re-running a paused or failed chapter job reuses completed segments and only
  synthesizes missing or voice-changed segments.
- `BookAudioGenerationService` analyzes stale or unanalyzed chapters and
  generates each non-skipped chapter in order. Each chapter uses the same
  persistent segment jobs, so an interrupted book run resumes completed work.
- WAV output is concatenated as PCM with short inter-segment pauses. The app can
  export a chapter WAV and play it in-app through an OpenAL Soft-backed service
  with progress, pause, resume, and stop controls. If no real local provider is
  ready, setup guidance is shown and generation remains unavailable; mock tones
  do not act as a production fallback.
- `FfmpegM4bExporter` normalizes chapter WAVs and creates an AAC M4B with
  per-chapter title and timing metadata. FFmpeg is an external executable,
  configured through `VBOOK_FFMPEG_EXECUTABLE` or `PATH`; VBook does not bundle it.
- OpenAL Soft is bundled through `Silk.NET.OpenAL.Soft.Native` and is licensed
  under LGPL-2.0-or-later; redistributed builds must preserve the license and
  applicable source-availability materials.

## Staged implementation

1. Architecture and EPUB import with chapter-order tests.
2. Avalonia book workspace, editable text, and transactional SQLite project storage.
3. Narrative models and local worker contracts with mock providers. (Done: the
   Narrative tab analyzes a chapter with the mock provider, lists characters and
   attributed segments, and persists them.)
4. Resumable per-chapter generation, voice profiles, WAV export, and in-app
   audio playback. (Implemented with default Qwen3-TTS and optional Kokoro and
   Piper providers; mock TTS remains for tests and development.)
5. (Implemented) Local OpenAI-compatible LLM analysis for book structure and
   chapter narrative, with reviewable roles, source validation, and SQLite
   persistence. Qwen3-TTS, Kokoro, and Piper speech synthesis are implemented
   behind `ITtsProvider`; Qwen and Kokoro assets are downloaded on request, and
   Piper's executable and voice models are installed separately.
6. (Implemented) Whole-book generation with chapter-level resume, and AAC M4B
    export with chapter markers through separately installed FFmpeg.

The solution targets .NET 10 (SDK 10.0.x). The Python worker environment is
Python 3.11.
