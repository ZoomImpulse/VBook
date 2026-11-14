# Local Audiobook Studio

A Windows desktop application for preparing local, multi-character audiobooks.
It imports EPUB books, supports source-preserving text and narration review, and
generates audiobooks locally. Original EPUB files are read in place and are
never modified.

## Current status

- Architecture and component boundaries are documented in
  [ARCHITECTURE.md](./ARCHITECTURE.md).
- Import EPUBs, browse chapters, edit extracted text, and save projects locally
  in SQLite. The latest project reopens when the application starts.
- Delete a project from the recent-project list with an explicit confirmation.
  This removes the local project data from the library but never deletes the
  original EPUB file.
- EPUB structure is prepared for speech: headings remain separate, ordered and
  unordered list items are announced, tables become spoken row summaries, code
  blocks are replaced with an omission notice, and explicit line breaks are
  retained for verse. The original EPUB is never changed.
- The **Structure** phase uses a local OpenAI-compatible language model to suggest
  a broad genre and a role for each imported chapter (front matter, body, back
  matter, or skip). The detected genre and current suggestions are saved with the
  project and restored when reopened. Suggestions are editable and affect chapter
  roles only after you choose **Apply reviewed roles**.
- **Produce → Preview & voices** uses the same local model to identify characters and
  quoted dialogue, then validates the returned spans against the source text
  before saving analysis in SQLite. Editing a chapter or changing its role marks
  the analysis stale until it is re-run. Model output is never applied as source
  text.
- **Analyze chapter** performs one combined AI check for narrative segmentation
  and spoken-wording suggestions. **Produce → Narration review** presents any suggested
  changes as tracked changes with original and proposed wording, a reason, and
  per-suggestion **Accept** / **Reject** controls. Accepted edits affect only
  generated audio; narrative segments and the chapter source remain anchored to
  the original text, and review decisions persist locally.
- Narration mode and voice assignments are saved per project. **Produce →
  Chapter audio** generates WAV chapter audio, stores completed segments in
  SQLite so interrupted jobs can resume, and supports WAV export and in-app
  play, pause, resume, and stop controls. **Publish** analyzes chapters as
  needed and generates every non-skipped chapter in order; the queue reflects
  saved analysis and audio-job readiness. Export a completed book as M4B with
  chapter markers using FFmpeg. Qwen3-TTS is the default speech engine, with
  optional Kokoro and Piper alternatives. Select a required book-wide speech
  language; individual voices inherit it by default and can use a supported
  language override. The selection is saved with the project. Each speaker can
  have a voice and (with Qwen) expressive direction. After analyzing book structure,
  use **Suggest** to generate an editable direction tailored to the detected genre
  and narrator or character role. Mock tones are used only by tests and
  development code.
- In-app playback uses OpenAL Soft through Silk.NET. The bundled OpenAL Soft
  native library is licensed under LGPL-2.0-or-later; distributions must retain
  its license and comply with the applicable source-availability requirements.
- No cloud APIs or telemetry are part of the design.
- Structure and combined chapter-analysis requests are sent to the configured
  local model endpoint only. By default, the app uses Ollama at
  `http://localhost:11434/v1/chat/completions` with model
  `frob/qwen3.5-instruct:9b`.
  Override these values with `VBOOK_LLM_ENDPOINT` and `VBOOK_LLM_MODEL`.

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
The workspace is organized into four book-level phases: **Prepare**, **Structure**,
**Produce**, and **Publish**. The production queue stays visible and reports
per-chapter analysis, narration-review, generation, and audio-ready states.
Select a chapter in the production queue to analyze or produce it. A section
marked **Skip** cannot be analyzed; select a narratable chapter or change its
role under **Structure** and apply the reviewed roles.
Produce groups chapter text, narration review, dialogue/voice preview, and
chapter audio; Publish handles resumable whole-book generation and M4B export.
The step guide offers a recommended next action while keeping every phase
directly accessible. Choose **System**,
**Light**, or **Dark** from the theme selector; the default follows the
operating-system appearance. The choice is saved in
`%LOCALAPPDATA%\LocalAudiobookStudio\settings.json`.

## Build

Build the solution from the repository root:

```powershell
dotnet build .\VBook.sln
```

Run the desktop application from Visual Studio or with `dotnet run` for the
`VBook.App` project. Ollama must be running locally and have the selected model
downloaded before using Structure or Narrative analysis.

### Local speech synthesis

Qwen3-TTS is selected by default. In **Produce → Speech engine**, choose **Set
up Qwen3-TTS** to install its isolated Python runtime and model. This setup
requires 64-bit Python 3.11–3.13 (or set `VBOOK_QWEN_PYTHON` to `python.exe`),
an NVIDIA GPU with working CUDA drivers, and about 10.5 GiB of free disk space.
The app installs pinned CUDA-enabled PyTorch/Qwen packages and downloads the
pinned Qwen3-TTS 1.7B CustomVoice model (about 4.5 GB). Setup is explicit and
cancellable. The model is Apache-2.0 licensed. Synthesis then runs locally on
the GPU; generated text and audio are not sent to a cloud speech service.
Qwen supports per-speaker voice, language, and optional expressive direction.

Kokoro is a smaller CPU alternative. Choose **Kokoro** in the speech-engine
selector and choose **Download Kokoro model** to install its quantized v1.0
voice pack (about 126 MiB). The download is explicit, cancellable, and verified
against the SHA-256 digest published by the sherpa-onnx release. Model files
are stored under `%LOCALAPPDATA%\LocalAudiobookStudio\models`; synthesis runs
locally after download and does not require network access.

Piper is an optional alternative. Install the Piper executable and one or more
compatible voice models separately. Each voice needs both its `.onnx` model and
matching `.onnx.json` configuration in the same directory. Configure both
environment variables before starting VBook:

```powershell
$env:VBOOK_PIPER_EXECUTABLE = "C:\tools\piper\piper.exe"
$env:VBOOK_PIPER_VOICES_DIRECTORY = "C:\models\piper"
dotnet run --project .\src\VBook.App
```

Select **Qwen3-TTS**, **Kokoro**, or **Piper** in the speech-engine selector.
If the selected engine is not installed/configured, VBook reports setup
guidance and disables audio generation rather than falling back to mock tones.
Piper appears as unavailable until its executable and at least one valid voice
are configured. Check the license of the exact engine version and each
voice/model before redistributing them: the maintained `OHF-Voice/piper1-gpl`
engine is GPL-3.0-or-later, while Piper voice licenses vary individually.

### M4B audiobook export

Install FFmpeg separately and ensure `ffmpeg` is on `PATH`, or set
`VBOOK_FFMPEG_EXECUTABLE` to its executable path. After **Generate whole book**
completes, use **Export M4B…** to create an AAC audiobook with chapter markers.
VBook does not download or bundle FFmpeg.
