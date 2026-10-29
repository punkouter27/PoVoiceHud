# Manual test

What the unit and integration tests cannot reach: a live microphone, a real Foundry resource, the window and the tray.

## First run

- [ ] With no `settings.json` or no stored key, the window opens on Settings with *Connect the fact checker*, Cancel greyed out, the header saying *Connect the fact checker to start*, and Windows' microphone indicator off.
- [ ] Type the endpoint, switch to the Azure portal to copy the key, come back: Settings are still open with the endpoint as typed.
- [ ] A wrong key → **Apply** says `✗ The key was refused (HTTP 401).` with a red dot and stays on Settings. The right key → the Live page, and the header goes through *Loading models…* to *Listening*.
- [ ] An endpoint that is not https → **Apply** says it must be an https:// URL.
- [ ] ⚙ → turn Wikipedia off → **Apply**: the header says *Settings applied* and the microphone meter keeps moving (no reload). ⚙ → change a switch → **Cancel** (or Esc): the switch is back as it was.
- [ ] Delete the key in Credential Manager (or revoke it) and restart: the app opens on Settings with the reason.

## Live and Facts

- [ ] "The Eiffel Tower was completed in 1889." → the line reads *Checking…*, then turns green with *Correct — …* within about 2 s, a rising two-note cue plays, and Facts has it with `en.wikipedia.org ↗ (looked up)`.
- [ ] "Python was created by Dennis Ritchie." → red, *Incorrect — …*, a low buzz, on Facts.
- [ ] "Octopuses dream in colour." → a quiet *Can't tell* note with no highlight, on the Live page only; the headline keeps the last real verdict.
- [ ] "The Empire State Building is in New York. It was completed in 1889." → *Incorrect*, and Facts reads "The Empire State Building was completed in 1889".
- [ ] "The current president of the United States is Joe Biden." → *Incorrect* (or *Can't tell* with Wikipedia off), not *Correct*.
- [ ] Pull the network cable mid-session and say a claim → the header says *Fact checks paused: …* once; the line goes plain. Reconnect and say another → *Fact checks are working again*.
- [ ] A run-on stretch ("Close your border. It'll be the end.") → one line per sentence. An opinion ("They don't love the art.") → a plain, dimmed line.
- [ ] "Can you hear me?" and "Let's move on." → plain lines, no highlight, no sound.
- [ ] Say the Eiffel Tower sentence again → Facts shows it once, marked ×2, at the top.
- [ ] Scroll the Live page up → new lines no longer pull it down. Scroll back to the end → it follows again.
- [ ] **Copy facts** → the button says *Copied*, and pasting gives one `✅ Correct: … — … (link)` line per fact.
- [ ] Minimize, maximize and restore → the pages fill the window and the Live page keeps its place. Close and reopen the app → it comes back at the same size and position.

## Code repository

- [ ] Settings → *Code repository* `https://github.com/<you>/<public repo>` → **Apply** says `✓ Repository found: N branches.`, and the header goes through *Fetching the code repository…*. Then, on the Code facts page:
- [ ] "How many pages are in the <repository name> website?" → an *Answer* on the Live line and on Code facts, with the files; the count matches the repository.
- [ ] "The <ClassName> retries three times." → *Correct* or *Incorrect* from the code; `<ClassName>.cs ↗` opens it on GitHub and **VS Code** opens it at `class <ClassName>`.
- [ ] "What does our website use for hosting?" (code words, no file named) → an answer from the infrastructure files.
- [ ] "The Eiffel Tower was completed in 1889." → still checked against Wikipedia, on Facts, not Code facts.
- [ ] **Copy** on Code facts → one `💬 Answer: …` or `❌ Incorrect: …` line each.
- [ ] A private GitHub repository without a token → **Apply** says `✗ Could not read the repository: …` and stays on Settings. With a token that can read contents → ✓.
- [ ] The same with an Azure DevOps URL as *Clone* copies it (`https://<org>@dev.azure.com/<org>/<project>/_git/<repo>`) and a PAT with *Code (Read)*; a Code facts link opens the file in Azure DevOps.
- [ ] A local folder that is a git repository → ✓ with its branch; any other folder → ✗.
- [ ] Offline, restart with a repository URL set → the app starts, and the header says the repository was not refreshed.
- [ ] Clear the repository → **Apply**: nothing is read; claims go to Wikipedia only.

## Window, audio and tray

- [ ] Mute with the microphone and with the tray's *Mute*: a falling chirp, the header says *Muted*, the level bars go dark, and nothing more is transcribed. A line that was *Checking…* goes plain. In Teams, Ctrl+Shift+M still mutes the call.
- [ ] Unmute and speak: the Live page's clock times carry on from the real time (they do not restart at 00:00).
- [ ] Play a video with speech → its lines are labelled *Them*; your own lines are labelled *You*.
- [ ] Drag the window by the headline, snap it with Win+Arrow, and resize it down to its minimum (420 × 320) → nothing is cut off; long pages scroll, and Settings keeps Apply and Cancel in view. The caption buttons do not cover ⚙ at 100 %, 150 % and 200 % scale.
- [ ] *Sounds and animations* off → no cues, and highlights appear without fading in; new lines do not slide in, the level bars do not breathe, and an incorrect claim does not wash the header red. *Cue volume* at 0 → silent; at 100 → as before.
- [ ] *Listen only during calls* on, no call → the header says *Waiting for a call* and nothing is transcribed. Join a Teams or Zoom call (or a browser call) → *Listening to the call on Teams* within 3 s; leave it → waiting again. The mute button does not change on its own.
- [ ] Compact (the button left of ⚙) → only the header remains, still on top; again → the pages come back at the old height.
- [ ] Two repositories on two lines → **Apply** reads both (✓ per line); a question naming a class only the second has → the Code row is tagged with the second's name.
- [ ] Hover the trend line in the header → the counts and *N model calls · … s average · … tokens*.
- [ ] Talk for a dozen lines, then mute → after a few seconds the header says *Meeting summary saved*; History shows it under *Summary of the meeting*. **Export** opens `Documents\PoVoiceHud\Meeting {date}.md`.
- [ ] Tray: left click hides and shows the HUD. *Open the history folder* opens `sessions`. *Copy diagnostics* puts a zip on the clipboard (logs, `settings.json`, `environment.txt`, no `sessions`). *Mute*/*Unmute* follows the header. *Quit* closes the app and removes the icon. *Quit PoVoiceHud* in Settings does the same.
- [ ] *Start with Windows* adds or removes `PoVoiceHud` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- [ ] *Not yet run on this machine:* `pwsh build/pack.ps1`, install `artifacts\releases\PoVoiceHud.App-win-Setup.exe`, raise `<Version>`, pack again with `-Feed <folder>`, set `UpdateFeed` to that folder and start the installed copy. The header should say the new version downloaded, and closing it should install that version.
