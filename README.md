# Deluded Voice Acting Generator

An AI voice-acting companion for **Deluded** (tested on 0.5.0). It gives the game's dialogue a speaker-aware performance through OpenAI Realtime voices, with local audio caching for smooth playback.

The companion is read-only: it uses Windows process enumeration and `ReadProcessMemory` to inspect the running game. It does not inject code, write game memory, modify files, or include any game files.

## Install

1. Download the latest Windows x64 ZIP from [Releases](https://github.com/gabrielpc4/Deluded-Voice-Acting-Generator/releases) and extract it anywhere outside the game folder.
2. Start `DeludedVoiceActingGenerator.exe`.
3. Enter your OpenAI API key beside **Open API Key:** and click **Save**. The key is stored only in the ignored local `openai-api-key.txt` file beside the app.
4. Start Deluded and load a dialogue. The companion looks for `SRTE-Win64-Shipping.exe`.

The release is self-contained for Windows x64; no source checkout or .NET runtime installation is required.

## Optional prologue audio cache

The app starts with an empty `cache` folder. To download already generated sounds covering most of the prologue, get the optional cache from [this Google Drive link](<g-drive-link-i-m-going-to-post-later>) and extract its contents into the app's `cache` folder. Existing cache files can be kept.

## Usage

- **Current Subtitle** plays when a spoken dialogue node appears.
- **Next Subtitle** is an exact graph prediction when available and is pre-cached but never auto-played.
- Press **R** to reset reader recovery and regenerate the line that was just spoken with a fresh voice session.
- For unknown speakers, use **M** or **F** when prompted; the selected voice applies for that conversation.

## How does it work?

The companion reads the game's live Unreal Engine dialogue widgets using standard Windows read-only process APIs. It validates the active dialogue and speaker text blocks, reads their `FText` contents, and follows the game's loaded dialogue graph to identify an exact next line when the branch is unambiguous.

Each line is matched to a character profile with a chosen OpenAI Realtime voice and acting instructions. The current line is played when it appears; an exact next line is generated or loaded ahead of time into memory, so advancing the dialogue can play it immediately.

Audio is keyed by normalized speaker and subtitle text, then stored as a local WAV cache. Cached files play locally even if OpenAI or the network is unavailable. Only uncached lines require an OpenAI connection. During an active conversation, each character keeps an independent Realtime session for short-term delivery context; those sessions are closed after dialogue ends.

The companion never writes to the game process, injects code, sends input to the game, modifies save files, or distributes any game data.

## Troubleshooting

- **No game detected:** start the supported game build, then load or enter a dialogue.
- **No audio:** confirm the API key is saved, your network is available for uncached lines, and Windows is using the intended output device.
- **Cached audio with no network:** cached WAV playback remains local and plays without waiting for OpenAI.
- **New game build:** dialogue memory offsets are build-specific, so a later game update may require companion changes.

## Privacy and safety

Your API key, audio cache, and diagnostic logs stay local and are excluded from this repository. Do not share them in issue reports or screenshots.

## License

MIT. See [LICENSE](LICENSE).
