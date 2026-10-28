# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@AGENTS.md

PoVoiceHud is an always-on-top WinUI 3 HUD (not a web app): it transcribes what is said with Whisper and fact-checks each claim as it goes, highlighting the sentence green or red. `README.md` is the one document: use, settings, files, and how it works with the design decisions behind it. `docs/manual-test.md` is the checklist for what tests cannot reach.

## Repo facts

- The only branch is `master`.
- Commits stay local: no remote is used for this project unless the owner says otherwise.
- `src/PoVoiceHud.Core` holds everything testable (audio, VAD, ASR, the fact checker, view models) and never references WinUI. `src/PoVoiceHud.App` is the XAML shell. `tests/PoVoiceHud.Core.Tests` is xUnit v3 on Microsoft.Testing.Platform.
- Build output is in `artifacts/`, not `bin/` and `obj/`.
- Warnings are errors, with analyzers at `latest-recommended`. Rules that are switched off are listed with their reason in `.editorconfig`; add to that list rather than suppressing inline.

## Commands

```powershell
dotnet build PoVoiceHud.sln
dotnet test --solution PoVoiceHud.sln                                      # everything, about 10 s
dotnet test --project tests/PoVoiceHud.Core.Tests -- --filter-class "*FactCheckerTests"   # one class
dotnet run --project src/PoVoiceHud.App                                    # the real app: opens the microphone
dotnet run --project src/PoVoiceHud.App -- --demo                          # a sample conversation, no audio (Debug)
dotnet run --project src/PoVoiceHud.App -- --demo --facts                  # the same, on the Facts page (Debug)
dotnet run --project src/PoVoiceHud.App -- --demo --code                   # the same, on the Code facts page (Debug)
dotnet run --project src/PoVoiceHud.App -- --demo --settings               # the Settings page, no audio (Debug)
dotnet run --project src/PoVoiceHud.App -- --demo --setup                  # the first-run page, no audio (Debug)
pwsh build/pack.ps1                                                        # Setup.exe + update packages in artifacts/releases (Velopack)
```

## How a sentence becomes a highlight

- **Pipeline** (`HudPipeline`): audio frames → `SileroVad` + `UtteranceSegmenter` → Whisper → `Sentences` (one line each, stamped with the clock time) → `FactChecker`. Transcription and checking are two separate channel stages, and up to `ParallelChecks` (3) claims are checked at once, so a slow model call never delays the next line. Each check gets the two sentences heard before it as context, checked or not. `Checking` says when a line is queued for the checker and when it is done; `Verdicts` carries each verdict with its line. A `FactCheckUnavailableException` becomes one status ("Fact checks paused: …"), not a verdict per line. Muting increments a generation counter; work already in flight is thrown away when it finishes. `Checker` can be replaced while it runs.
- **FactChecker**: `IsCandidate` (generous: three words, numbers count, not only fillers, not a plain question) → Wikipedia evidence → `FoundryClient` → a verdict with the claim restated to stand alone, or null for `not_a_claim`. The prompt carries today's date. A claim about now ("current", "still"…) keeps a verdict only with a looked-up article (the model's knowledge ends in mid-2024). The cache key includes the context when the claim says "it", "they" and so on. A model that cannot be asked throws `FactCheckUnavailableException` (`NeedsSetup` for a refused key or missing deployment); the content filter refusing one sentence is a "can't tell". `Connection.TestAsync` is the one-call check used by Apply and at start. `FoundryClient.Model` is fixed (`gpt-4.1-mini`) and the reply is held to a JSON schema.
- **Words**: the `Verdict` enum keeps the model's terms (Supported, Contradicted, Unverified); everything a person reads uses `Verdict.Label()`: Correct, Incorrect, Can't tell.
- **View models**: `HudViewModel` holds `Live` (`LiveLineViewModel`: Heard → Checking → Correct / Incorrect / CantTell) and `Facts` (`FactViewModel`, correct and incorrect only, newest first, repeats counted), and `CodeFacts` (`CodeFactViewModel`: answers and code verdicts with their files), and which page shows: `Page` (Live, Facts, Code) or Settings (`IsSettingsOpen`, or `NeedsSetup` until a working endpoint and key exist). A code answer puts the line in `Answered`. The window is a standard one (resizable, kept on top) with the header drawn into the title bar; the pages fill it.
- **Wiring** is all in `ServiceCollectionExtensions.AddPoVoiceHudCore`; `CreateChecker` builds the fact checker on its own too.
- **Code repository** (`Repos/`, optional): `RepoSource` clones a GitHub or Azure DevOps URL into `%LOCALAPPDATA%\PoVoiceHud\repos` (LibGit2Sharp, token from `CredentialStore.RepoToken` as the password) or reads a folder, `Probe`s it for Apply, and builds web `Link`s to files. `CodeRepository` keeps a `SymbolIndex` (tracked files, EF entities) and the files' text fresh with a file watcher, and finds excerpts: `Named` (files a sentence names, via `SymbolMatcher`) and `Search` (best text matches); `Editor` builds `vscode://file/…:line` links. `CodeAnswerer.IsAbout` routes a sentence to the code (repository name via `SymbolMatcher.Says`, a named file, or code words such as "our website"), and `AskAsync` returns a `CodeFact` (answer, or verdict for a statement; a sentence ending in "?" always gets an answer) using `FoundryClient` with `CodeAnswerer.ReplyFormat`. The pipeline sends those sentences there (questions too) and publishes `CodeFacts`; it also feeds Whisper's vocabulary. `App.StartAsync` opens the repository before building the container; `CreateCodeAnswerer` is swapped in with the checker.
- **Settings changes**: Settings is a page with Apply and Cancel (`SettingsViewModel`); Apply tests a new endpoint or key with one call before saving, and leaving the page without Apply reverts it. Without a working endpoint and key the app shows only Settings and never opens the microphone; `StartAsync` also tests the connection and goes back to setup if the key is refused. `App.ApplyAsync` then starts the pipeline after setup, swaps in a new checker when a pipeline is running and the repository and its token are unchanged (the microphone and the call are always both captured), and otherwise disposes the whole DI container and builds a new one. A run counter stops a slower, older start from installing its pipeline. The only state is `settings.json` and Credential Manager (`CredentialStore`); for secrets, follow the existing Credential Manager pattern.
- **Feedback**: `SoundPalette` cues (each ≤ 240 ms, under the segmenter's 300 ms floor) and a `BrushTransition` on the highlight, both under the one *Sounds and animations* switch. There are no shaders or particle effects any more.

## Checking a UI change

The app is a desktop window, so the browser tools cannot drive it. Use the `--demo` forms above and capture the window (`PrintWindow` with `PW_RENDERFULLCONTENT` works without focus); do not send synthetic keys or clicks to the desktop, because the owner is usually working on it. A `--demo` run does not save its window placement. Running without `--demo` opens the microphone and appends what it hears to `%LOCALAPPDATA%\PoVoiceHud\sessions`.

## Limits worth knowing

- 100 unit tests and 50 integration tests at most (`[Trait("Category", "Integration")]` marks the latter). The suite is at 68 and 22.
