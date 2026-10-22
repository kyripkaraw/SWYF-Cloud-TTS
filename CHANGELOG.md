# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
versions follow [Semantic Versioning](https://semver.org/).

## [1.3.0] - Unreleased

### Added
- `SWYF-CloudTTS-Setup.exe`: one-file installer with Install / Repair / Update / Uninstall, an
  entry in Windows "Apps & features", English, Ukrainian and Russian UI, and `--quiet`/`--status`
  for scripts. It elevates only when the game folder is not writable.
- CHARACTERS list in the in-game card: play the game's own clip, name a character and pick its
  voice. The worker picks up changes from the next line without a restart.
- The in-game plugin now compiles against the game's own `mscorlib`, so APIs missing from the
  game become build errors instead of runtime crashes.
- ElevenLabs clone rotation: when the voice slots are full (10 on Starter), the clone heard least
  recently is deleted and the new character is cloned. Only `swyf-...` voices are touched, unused
  duplicates go first, existing clones are reused, **Keep** protects a character (the boss), and
  the last 5 monthly voice additions are left to the player. State lives in `elevenlabs-clones.json`
  and the card shows slots and the monthly budget.
- ElevenLabs speaks through the streaming endpoint, so the first audio arrives sooner.
- Characters without an ElevenLabs clone get one of 21 default voices by gender and pitch
  instead of one stock voice for everyone.
- The in-game card in English, Ukrainian and Russian ("auto" follows the game in Russian and Windows
  otherwise), built from the game's own setting rows, classes and Cyrillic font so it looks like part
  of the game. Switches are On/Off lists like the game's, and model and voice have one list each with
  "Other" for any id.
- **Cloud voices for** in the card edits the game's own Qwen preference, so English calls can use cloud
  voices too; the game's duplicate row is hidden while the card is there. The card also warns while the
  game is still downloading its voice files or after it switched cloud voices off for the session.
- ElevenLabs Free plan support and instructions: default voices for everyone, plus **Load my voices**
  (`--list-voices`) that offers the player's own Voice Design voices by name in the card.
- `SWYF_TTS_STATE_DIR` for tests, `--speaker <id>` for `--test`, and `tools/fake_elevenlabs.py`
  (`--free`, `--designed`, `--stall`).

### Changed
- Automatic per-character voices are no longer written to `voices.json`; changing the global voice
  now affects every character without an explicit choice.
- The config always lives in `%LOCALAPPDATA%\SWYF-CloudTTS`; old copies inside the game folder
  (which could hold API keys) are moved there and deleted.
- One version number for all parts, set in `Directory.Build.props`.

### Removed
- Hume AI (Octave): Hume shuts down its TTS and EVI APIs on 13 November 2026.
- The browser settings panel, its Start Menu shortcut and the PowerShell install scripts; the
  in-game card and the setup replace them.

### Fixed
- **Test voice** in the game generated audio but could not play it
  (`Method not found: BitConverter.ToSingle`).
- A diagnostic path in the plugin used `Stack<T>`, which the game does not ship.
- Test phrases ending in a backslash broke the worker's command line.
- An unknown provider name fell back to OpenAI instead of the default Google AI.
- After the ElevenLabs voice limit was reached, every line of a new character retried cloning
  (the game restarts the worker for each line), adding ~3 s before each of those lines.
- A request timeout after audio had started was retried, which could repeat part of a line;
  the timeout now counts from the last audio received.
- A slow service could keep a line silent for longer than the game's 20 s first-audio limit, after which
  the game turns Qwen off for the whole session. A line now gets silence after 15 s instead, and the
  local fallback is only tried when the cloud fails within the first 5 s.
- Slider values in the card stayed empty, the 1.2 default ElevenLabs voice showed as a raw id, and the
  account line lagged one clone behind.

## [1.2.0] - 2026-10-22

### Added
- Providers: Google AI (Gemini TTS), OpenAI, ElevenLabs (with per-character cloning),
  Hume AI Octave, self-hosted Qwen3-TTS (`clone`, `voice`, `design` modes).
- Per-character voices recognised by reference-clip hash, with gender-based auto assignment.
- Browser settings panel and the in-game **CLOUD TTS** card in *Settings → Speech*.
- Audio cache, retries within the game's 30 s limit, silence instead of errors.
- Diagnostic providers: `mock`, `silent`, `passthrough`.

[1.3.0]: https://github.com/kyripkaraw/swyf-cloud-tts/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/kyripkaraw/swyf-cloud-tts/releases/tag/v1.2.0
