# Deluded (SRT) Voice Acting Generator
<img width="1983" height="793" alt="readme-header" src="https://github.com/user-attachments/assets/31bb6b8b-dd35-4cb3-9e5a-590ccbba269f" />

A program that reads the game's subtitles in real time and uses AI to generate realistic voice acting to the characters.

<img width="750" src="https://github.com/user-attachments/assets/a58629ed-0778-4bbc-8f55-89fe0c4c12e8" />

It uses OpenAI RealTime API to generate the voices, each character has a custom prompt that makes the delivery match the essence of each character. 

<img width="750" alt="ui-voice-profiles" src="https://github.com/user-attachments/assets/73b5564a-152c-4d61-842e-ace309b38ae6" />


This can be accessed under Configure -> Voice Cast Profiles"

# How to Install and use it:

1. Download the .zip file from the latest release: https://github.com/gabrielpc4/deluded-srt-voice-acting-generator/releases
2. Extract it *anywhere* you like. Then open "DeludedVoiceActingGenerator.exe"
3. You will need an [OpenAI key](https://platform.openai.com/api-keys) to generate new audio, you need to have *[credits](https://platform.openai.com/settings/organization/billing/overview)* on your OpenAI API account as well. However, you can download all the audio that I've already generated on my single run of the game by clicking in the *"Download Cache"* button in the program, or downloading the audios from my google drive: https://drive.google.com/drive/folders/18P3Bjujbh2KCfTUjX8j1uCtwOm3dzo9_?usp=sharing and placing them in the "cache" folder (create it). That way you will only generate audio of paths that I haven't chosen in the game, saving tokens.
4. Open the game with the program opened (or open the program after you open the game, the order doesn't matter). You should start hearing audio when the subtitles appear.

Your OpenAI key is only stored locally at the folder you extracted the program, in a open-ai-key.txt file.

You can use the program without an OpenAI key, but you will have to download the cache and will be limited to only the dialogues that I've encountered during my gameplay.

# Tips
- Press the "R" key at any time to re-generate the audio of the current subtitle that it's being displayed.
- This can be also used as a trick if the AI refuses to speak the line because of the guidelines, it resets the memory of the current conversation so the AI doesn't know what it was talking about before, so it reduces the changes of getting rejected due to the previous context.
- Also if you don't like the intonation used, press "R" and the AI will try again and will probably give you a different intonation.

## License

MIT. See [LICENSE](LICENSE).
