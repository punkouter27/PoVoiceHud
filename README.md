# PoVoiceHud

An always-on-top Windows 11 HUD that fact-checks what is said, as it is said. It transcribes your microphone and the call locally with Whisper. Each sentence that states a fact is checked by `gpt-4.1-mini` on your Azure AI Foundry resource, using Wikipedia articles as evidence. The sentence then turns **green** (correct) or **red** (incorrect), with the reason underneath. With a code repository connected, talk about the code is looked up in it: a question gets an answer and a statement a verdict, listed on the **Code facts** page.

Audio never leaves the machine. Only the text of claims goes to your Foundry endpoint, and their key words go to Wikipedia's and Wikidata's search, and for a claim about the present to GDELT's news search (switch all three off with *Look claims up* in Settings). With a code repository connected, a sentence about the code goes to the endpoint with the list of the repository's file paths and up to three excerpts of its files (6,000 characters each). A meeting summary sends that day's transcript to the endpoint, only when you ask for one or mute after a conversation.

## Using it

- **First run:** the window opens on Settings, and the microphone stays off until an endpoint and key pass the connection check (see below). The same happens at a later start if the key is refused or the deployment is gone.
- **The window** is an ordinary one: drag it by the headline, resize, snap, minimize, maximize or close it. Closing quits, so nothing listens after the window is gone. It stays on top of other windows so it can sit beside a call.
- **The header** is the title bar: the microphone with a level meter (its bars breathe while a claim is being checked), the latest verdict or status, a spinner while a claim is being checked, a small line of the share found correct after each recent verdict (red when most are wrong), the compact button and ⚙. Click the microphone, or use *Mute* in the tray menu, to mute. There is no global hotkey, so no shortcut of Teams or another app is taken. Muting drops anything not yet transcribed. The bar along the header's bottom edge is this session's verdicts as shares of green, red and amber. The tooltip of either gives the counts and how the model has done: calls, average time, tokens, retries and failures. A claim found incorrect washes the header red for a moment.
- **Compact** (the button left of ⚙): the window shrinks to its header, a strip to keep beside a call; the same button brings the pages back.
- **Four pages**, switched at the top of the window. All keep a readable width when the window is wide.
  - **Live:** every sentence as it is heard, one per line, with the clock time; the newest slides in. A claim reads *Checking…* while the model works, then is highlighted *Correct* or *Incorrect* with the reason. A specific claim the model cannot settle gets a quiet *Can't tell* note. Questions, requests, opinions, predictions, remarks about your own company or team, and garbled speech are not claims: they stay plain and dimmed. The page follows the newest line unless you scroll up. **Names** renames *You* and *Them* for this call ("Alex"), on every page at once; the history keeps the plain sides.
  - **Facts:** only the claims found correct or incorrect, newest first. Each is restated to stand on its own ("It was finished in 1889" becomes "The Eiffel Tower was finished in 1889"), with its reason, its source, when it was said, and a thin bar for how sure the model was. A claim said twice is one row marked ×2, showing when it was said last. Filter the list by words or a speaker's name, and by kind (correct, incorrect, answers, said by you or by them). **Copy** puts the list as filtered on the clipboard.
  - **Code** (Code facts, with a code repository connected): a sentence is about the code when it says a repository's name ("the Punkouter software website"), names a file, class or entity in it ("the invoice service"; a one-word name only for a code file, and short words must match exactly, so "we" is never web.config), or uses words like *our code*, *the codebase*, *the repo*, *our app*, *our website*, *the backend*, *the API* or *the database*. A sentence right after one of those counts too when it points back or asks ("Tell me about our repo." then "How many pages are in it?"); if it turns out to be about something else, it gets the ordinary check. Then, questions and requests ("tell me about…") included, it goes to the code rather than to Wikipedia: the model gets the list of every file path, the files the sentence names and the files whose text best matches its words. A question ("How many pages are on our website?") gets an answer and a statement ("The invoice service retries three times") a verdict; talk the files say nothing about is dropped. The Live line shows it, and Code facts lists answers, correct and incorrect (a *Can't tell* stays a quiet note on its line, as on Facts), newest first, with the files it rests on: each opens on GitHub or Azure DevOps (or on disk for a folder), and **VS Code** opens it at the line it is about (for a URL, that is the app's copy in `repos\`, which the next refresh overwrites); each file has an icon for its kind (code, a page, a document, settings, data). With several repositories connected, the sentence goes to the one whose name it says, else the one with the files and classes it names, else the one whose text matches it best, and the row says which. The page filters and copies as Facts does.
  - **History:** any day from the session log, searchable. **Summary** sums the day up with the model (main points, decisions, corrections, open questions, and what was asked or decided about the code) and keeps it beside the day's history; muting after a conversation (a dozen lines or more since the last summary) does the same in the background. **Export** saves the day with its summary as `Documents\PoVoiceHud\Meeting {date}.md` and opens it.
- **When checking fails** (the service is down or slow, or the key stops working), the header says so once, *Fact checks paused: …*, and the lines stay plain. It says *Fact checks are working again* when they are. A sentence that Azure's content filter refuses gets its own quiet *Can't tell*.
- **Sources:** a link to an article the app looked up itself is marked *(looked up)*. A URL the model wrote is marked *(cited by the model)*, because it may not exist.
- **Tray icon:** left click hides or shows the HUD. The menu has *Mute*/*Unmute*, *Open the history folder*, *Copy diagnostics*, *Check for updates* and *Quit*.

## Settings (⚙)

| Setting | |
|---|---|
| **Azure AI Foundry endpoint** | `https://<resource>.services.ai.azure.com`. The resource needs a deployment named `gpt-4.1-mini`. |
| **Key** | Kept in Windows Credential Manager (`PoVoiceHud/FoundryKey`), never in a file. |
| **Code repositories (optional)** | One per line: a GitHub URL (`https://github.com/org/repo`), an Azure DevOps URL (`https://dev.azure.com/org/project/_git/repo`, as *Clone* copies it) or a folder on this PC that holds a git repository. A URL is cloned into `repos\` and refreshed at each start (if that fails, last time's copy is used and the header says so). A folder is watched: a saved file is read again on its own, a commit or a new file rebuilds the index. Their names and file names also go into Whisper's vocabulary. **Apply** reads each new repository first and says what it found. See *Code* above for what they are used for. |
| **Repository access token** | Only for private repositories (one token for all): a GitHub personal access token with read access to contents, or an Azure DevOps one with *Code (Read)*. Kept in Credential Manager (`PoVoiceHud/RepoToken`). |
| **Look claims up** | On: up to three Wikipedia article intros, Wikidata's key facts (dates, heights, populations, who leads what) for the thing the claim names, and for a claim about the present ("current", "still"…) up to three recent news headlines from GDELT go to the model as evidence, all at once (2.5 s limit each; a slow or failed one is left out). Off: nothing goes to any of them. |
| **Start with Windows** | |
| **Listen only during calls** | Hear nothing until a call app (Teams, Zoom, Slack, Webex, Discord, Skype, a browser call…) has a microphone open, found from Windows' audio sessions every 3 s, and stop when it closes it. The header says *Waiting for a call* meanwhile; the mute button stays as you left it. |
| **Sounds and animations** | A short cue per verdict, a bell for an answer from the code, and one for mute (each under 240 ms, so call capture never transcribes one); the highlight fading in, the newest line sliding in, the level bars breathing while a claim is checked, and the red wash for an incorrect claim. Windows' *Animation effects* switch also turns the animations off. **Cue volume** sets how loud the cues are. |

Settings is a page in the window, so switching to the Azure portal to copy a key leaves it as it is. **Apply** saves and uses the changes without a restart. When the endpoint or key changed, it first sends one message to the model; if that fails, it stays on the page and says why (key refused, no such deployment, unreachable). **Cancel** (or Esc, or ⚙ again) puts back what was applied. Applying only replaces the fact checker: Whisper and the audio keep running. The page's corner shows when the copy was built (`Build YYMMDD`) and its version. `%LOCALAPPDATA%\PoVoiceHud\settings.json` holds the above plus three keys for the file only: `WhisperThreads`, `Vocabulary` (comma-separated names Whisper should expect) and `UpdateFeed`.

## Files

| | `%LOCALAPPDATA%\PoVoiceHud\` |
|---|---|
| Settings | `settings.json` |
| History | `sessions\{date}.jsonl`: every line heard and every verdict, on this PC only; `sessions\{date}-summary.md`: that day's meeting summary, when there is one |
| Exports | `Documents\PoVoiceHud\Meeting {date}.md`, written by *Export* on the History page |
| Logs | `logs\{date}.log` (14 days) and `crash.log`. *Copy diagnostics* zips these with `settings.json` and a machine description, but never the history or the key. |
| Repositories | `repos\`: the clone of a repository URL, one folder each |
| Models | `models\`: `ggml-base.en.bin` (~148 MB) and `silero_vad.onnx`, downloaded on first run and checked against a pinned SHA-256 |

## Build, run, test

```powershell
dotnet build PoVoiceHud.sln
dotnet run --project src/PoVoiceHud.App                         # the real app: opens the microphone
dotnet run --project src/PoVoiceHud.App -- --demo               # a sample conversation, no audio (Debug builds)
dotnet run --project src/PoVoiceHud.App -- --demo --facts       # the same, on the Facts page
dotnet run --project src/PoVoiceHud.App -- --demo --code        # the same, on the Code facts page
dotnet run --project src/PoVoiceHud.App -- --demo --history     # the History page (reads the real session log)
dotnet run --project src/PoVoiceHud.App -- --demo --compact     # the header alone
dotnet run --project src/PoVoiceHud.App -- --demo --scripted    # sentences through the real routing, checker and code answerer, with a canned model and this repo
dotnet run --project src/PoVoiceHud.App -- --demo --settings    # the Settings page
dotnet run --project src/PoVoiceHud.App -- --demo --setup       # the first-run page
dotnet test --solution PoVoiceHud.sln                           # 94 tests: 71 unit + 23 integration (models, Credential Manager, git)
pwsh build/pack.ps1                                             # Setup.exe + update packages in artifacts/releases (Velopack)
```

Requirements: Windows 11, .NET 10 SDK, a microphone. No GPU is needed. Build output goes to `artifacts/`.

**Install and update:** Setup.exe installs per user (no admin) to `%LOCALAPPDATA%\PoVoiceHud.App`. It is not code-signed, so SmartScreen warns the first time (**More info → Run anyway**). Raise `<Version>` in `src/PoVoiceHud.App/PoVoiceHud.App.csproj` before packing, and keep `artifacts/releases` between versions for delta updates. With `UpdateFeed` set to an https URL or a folder (publish with `pwsh build/pack.ps1 -Feed <folder>`), an installed copy downloads a newer version in the background and installs it when it closes.

## How it works

`src/PoVoiceHud.Core` has everything testable and never references WinUI. `src/PoVoiceHud.App` is the WinUI 3 window, tray icon and Velopack entry point. `tests/PoVoiceHud.Core.Tests` is xUnit v3.

1. `WasapiDualCapture`: 16 kHz mono frames from the mic and the speakers' loopback, mixed. There is no switch for this: the other side of a call is always heard and checked too, and each line is labelled *You* or *Them* by which input was louder (*?* when neither was). Anything else playing on the default communications device is heard as well. Use a headset: on open speakers the microphone hears the call a second time.
2. `SileroVad` + `UtteranceSegmenter`: one utterance per sentence. Anything under 300 ms is dropped, which is why the cues stay shorter.
3. `WhisperTranscriber`: `base.en` on the CPU. `HudPipeline.Sentences` splits what it heard into sentences (not at initials or titles), and each one is a line on the Live page, stamped with the clock time it started.
4. `FactChecker.IsCandidate`: a generous word-list check. It sends on every sentence that has at least three words (numbers count), is not only fillers, and is not a plain question. "…, right?" after a statement still goes; the model's `not_a_claim` settles requests and chatter.
5. A sentence about the code repository goes to `CodeAnswerer` instead (step 4 does not apply, so questions go too): the file list, the files named (`SymbolMatcher` on `CodeRepository`'s index) and a text search, to `FoundryClient` with its own reply schema (question or statement, evidence, answer or reason, then the verdict, files). The verdict comes after the evidence so the model looks before it decides, and a statement keeps *Correct* or *Incorrect* only when its evidence is a quote of at least 12 characters from an excerpt it was sent, or a path it was sent; otherwise it is *Can't tell*. Whisper's common mishearing of "our" as "are" ("which model does are app use") still counts as code words, and a question Whisper ends with a full stop is still a question. With several repositories, `CodeAnswerer.Pick` chooses one per sentence. Any other candidate goes to `FactChecker`: evidence from Wikipedia, Wikidata and (about the present) GDELT news, all at once (`EvidenceSources`), then `FoundryClient` (`gpt-4.1-mini`, verdict JSON schema with the claim, reason and source before the verdict and a 0–1 confidence after it, 8 s per attempt, retried on 429/5xx; each call's time, tokens, retries and failures counted in `ModelStats`). The sentence travels as JSON data with today's date and the two sentences heard before it as context, so speech cannot pass as instructions. A sentence that says "it" or "they" is looked up together with the one before it. The model restates the claim to stand on its own. The prompt counts opinions, predictions, anecdotes, remarks about the speaker's own company or team, vague remarks and garbled speech as `not_a_claim`, and those lines stay plain. Verdicts are cached per claim, and per context when the claim leans on it.
6. `HudViewModel`: the line takes its verdict, and correct and incorrect claims go to Facts; the header's trend line and cue follow each verdict. Everything heard and checked is appended to the session log, which the History page reads back.

Transcription and checking are separate stages, and up to three claims are checked at once, so a slow model never delays the next line or the next claim. Muting bumps a generation counter, and anything still in flight is discarded. A checker that cannot be reached throws `FactCheckUnavailableException`, which the pipeline reports once as a status.

**Design notes**

- **Whisper on the CPU** with a 10 s audio context: `base.en` takes about 300 ms per sentence on 8 threads, with no GPU driver dependency. `tiny.en` was faster but wrong. A vocabulary prompt adds about 80–100 ms, so it is kept to 120 characters.
- **A fixed cloud model:** `gpt-4.1-mini` answers in about 1–1.5 s and honours the JSON schema. `Llama-3.3-70B-Instruct` was as fast on average but now and then ran past the 8 s limit.
- **Wikipedia as evidence:** a model judging alone invents sources. With the article intros it cites a page the app fetched itself, which the Facts page can mark *(looked up)*. Wikidata adds the exact numbers and dates a model's memory slips on. For the present, GDELT's news search is open data meant for this; Google's and Bing's news feeds answer faster but their terms allow only showing them in a personal feed reader.
- **A deterministic pre-filter, not an LLM router:** it costs nothing and is easy to test. It drops only what cannot be a claim: a missed claim costs more than a model call, and `not_a_claim` catches the rest.
- **Reason, then decide:** both reply schemas put the verdict after the reasoning (and the evidence, for the code), since a model writes its properties in schema order. With the verdict first it decided, then explained: "the app checks facts with Llama" came back correct because gpt-4.1-mini is "a language model similar to Llama".
- **JSON without reflection:** settings, the session log and the window placement go through a source-generated `CoreJson` context, and prompts are built from JSON nodes, so `PoVoiceHud.Core` passes the trimming and Native AOT analyzers (`IsAotCompatible`). `Settings` keeps plain setters: the generated reader fills every `init` property, so a key missing from an older file would lose its default. A full Native AOT publish of the app also needs Visual Studio's C++ build tools for its linker.
- **The present needs a source:** `gpt-4.1-mini`'s knowledge ends in mid-2024. Even when told the date, it judged "the current president is…" from its training. So a claim about now ("current", "still", "latest"…) keeps *Correct* or *Incorrect* only when it rests on an article looked up for it; otherwise it is *Can't tell*.
- **Settings check before they apply:** an endpoint or key is saved only once the model has answered with it, and the app does not listen until one has. It never runs in a state where every claim fails.

Working rules for coding agents are in `AGENTS.md`; `docs/manual-test.md` is the checklist for what the tests cannot reach.
