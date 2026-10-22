# Roadmap

Status as of 1.3.0 (October 2026). Ordered by value for players.

## Before the first public release

- [ ] **Play-test 1.3.0 in a clean game** (Steam *Verify integrity* first: a modified
      `Assembly-CSharp.dll` with longer Qwen timeouts hides the stock 20 s limit): install with the setup, check the CHARACTERS list,
      Test voice playback, Apply, Repair after a simulated update, uninstall.
- [ ] **Screenshot of the in-game card** for the README (only the setup window is shown now).
- [ ] **Decide the recommended provider.** Gemini's free tier allows only a few TTS requests per
      day, so new players hit `429` within minutes. Either recommend a paid provider by default or
      explain the limit right in the card.
- [ ] Create the GitHub repository (sources only: `analysis/` and game files stay out, see `.gitignore`)
      and publish `SWYF-CloudTTS-Setup-v1.3.0.exe` with its `.sha256` on Releases.

## 1.4 — Fewer support questions

- [ ] **Skip the 1.3 GB local model download.** The game will not start the worker until the Qwen
      GGUF files are downloaded and verified, although the cloud worker never reads them. Marking the
      download as done from the plugin would save low-end players the download (needs in-game testing).
- [ ] **ElevenLabs Voice Design for chosen characters**: describe a voice ("old grumpy boss") and
      design it with the game clip as reference (`eleven_ttv_v3`, uses a slot, protected by Keep).
- [ ] **Survive Steam updates automatically.** The Doorstop entrypoint runs before the game starts
      speech: if the worker in the game is not ours, copy `%LOCALAPPDATA%\SWYF-CloudTTS\bin\QwenTtsWorker.exe`
      back and patch `runtime-manifest.json`. Removes the need to press Repair.
- [ ] **Clear errors in the card**: "key invalid", "daily quota used up", "no internet", shown
      next to the provider instead of only in `worker.log`.
- [ ] **Game voices when the quota runs out.** A used-up quota or an invalid key now gives silent lines;
      answering with a worker error instead would make the game fall back to its own voices for the
      rest of the session.
- [ ] **"Collect logs" button** that zips the logs and the config with masked keys for bug reports.

## 1.5 — Trust and quality

- [ ] **Store API keys with Windows DPAPI** instead of plain text in the config.
- [ ] **Code signing** of the setup and the worker (for example SignPath's free plan for open
      source) to calm SmartScreen and antivirus tools.
- [ ] **Automated tests in CI** for the worker: protocol round trip with the `mock` provider and
      provider requests against `tools/fake_*.py`.

## Later

- Emotion and style per line where providers support it.
- Shareable voice presets ("my cast") for `voices.json`.
- More providers: Azure Speech, Google Cloud TTS, a local low-CPU option for offline play.
- Update check in the setup (notify only).

## Known risks

| Risk | Mitigation |
|---|---|
| A game update changes the worker protocol | The worker validates every field like the original and logs `Invalid Qwen ...`; ship a mod update. |
| A game update renames `PauseMenuToolkitController` or `speech-screen` | The card simply does not appear; voices keep working. |
| The game starts verifying files beyond `runtime-manifest.json` | Uninstall or Steam *Verify integrity* restores stock behaviour. |
| Provider free tiers change | Keep several providers working; show quota errors clearly. |
