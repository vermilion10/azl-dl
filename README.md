# azl-dl

azl-dl downloads Azur Lane's game data and assets from the official EN servers and decodes them on your own machine. It comes as a desktop app for browsing ships, equipment, stories and audio, and as a command-line tool for bulk downloading and extracting.

Everything is read from the game's own files: ship stats, voice lines, card art, music and subtitles come straight from the client data, not from a fan database.

## Features

### Desktop app

- **Ships.** A card gallery in the game's own rarity frames, with filters for rarity, collection (Standard, Research, META, Collab), fleet, hull type and faction. Each ship page shows:
  - stats at any level from 1 to 125, with affinity, enhancement (or development levels and META repairs) and retrofit;
  - skills;
  - skins, with paintings, face expressions and voice lines;
  - animated chibi, dynamic skin and Live2D models.
- **Equipment.** Every codex entry with its stats at each enhancement level, speciality, ammo and the hull types that can equip it.
- **Juustagram and Fleet Chat.** The ships' social posts with comments and replies, and the in-game chats, which branch on the replies you pick.
- **Music.** The in-game music collection by album, plus every music track the game ships. Play tracks or save them as WAV.
- **ASMR.** The game's sound stories, played with their subtitles. The line being spoken is highlighted, and clicking a line jumps there.
- **Story.** The story archive, readable chapter by chapter.
- **Assets.** A browser for every downloaded bundle and file, with previews:
  - pictures, text and data tables;
  - sound bank cues, which can be played;
  - the field values of any other Unity object.

  Anything can be saved, and a whole bundle can be extracted.
- **Sync.** Updates the game data and shows each asset category's size and download state.
- **Three visual styles** (Chart room, Registry book, Port menu). Light or dark follows your system setting unless you pick one.

### Command-line tool

| Command | What it does |
|---|---|
| `versions` | Ask the game server for the current version of every asset category. |
| `hashes` | List the files in a category's current file list. |
| `download` | Download a category's files, skipping ones that are already current. |
| `update` | Download and decode the current game data (tables, scripts, stories) in one step. |
| `extract` | Convert downloaded bundles to PNG images and files; paintings are reassembled from their tiles. |
| `audio` | Decode downloaded voice banks into WAV files, one per cue. |
| `config` | Decode the downloaded data tables into JSON. |
| `scripts` | Decrypt the scripts bundle and write out every Lua script it holds. |
| `sharecfg` | Run the configuration scripts and write the tables they build as JSON. |
| `story` | Decode the story scripts into JSON. |
| `inspect` | List the objects inside one downloaded bundle. |

Run `azl-dl <command> --help` for each command's options.

## Requirements

- The [.NET SDK](https://dotnet.microsoft.com/download) 9.0.200 or newer to build. The programs target .NET 8, so the .NET 8 runtime is enough to run them.
- Windows 10 or 11. The app is built on Avalonia, which also targets macOS and Linux, but it is tested on Windows only. Voice lines, music and sound stories play through the Windows audio API; elsewhere they can still be saved as WAV.
- An internet connection for downloading game data and assets.

## Getting started

Build both programs from the repository root:

```bash
dotnet build -c Release
```

Start the desktop app:

```bash
dotnet run -c Release --project src/AzlDl.App
```

On first start the app has no game data yet:

1. Open **Sync** and update the game data (about 150 MB). The app then reads ships, equipment, stories and the rest from it.
2. Card images, paintings, voice lines and other assets download when you first open them, or in bulk from the button above each gallery.

By default the app keeps its data in your local application-data folder (`%LOCALAPPDATA%\azl-dl\data` on Windows). Pick another folder in **Settings**, for example one already filled by the command-line tool.

## Using the command-line tool

```bash
dotnet run -c Release --project src/AzlDl.Cli -- versions
```

Update the game data into a folder of your choice:

```bash
dotnet run -c Release --project src/AzlDl.Cli -- update --data ./output
```

Download and extract one ship's paintings, for example Enterprise (`qiye`):

```bash
dotnet run -c Release --project src/AzlDl.Cli -- download painting qiye
```

```bash
dotnet run -c Release --project src/AzlDl.Cli -- extract painting/ qiye
```

The commands share one folder layout: `AssetBundles` for downloaded files, `art` for extracted images, and JSON tables under `config`, `sharecfg` and `story`.

## Animated viewers

The chibi, dynamic skin and Live2D viewers use web runtimes that azl-dl doesn't ship: Spine Runtimes 3.8, Live2D Cubism Core, PixiJS and pixi-live2d-display. The first time you open a viewer, the app lists each runtime with its license and downloads them from their official hosts only after you agree. The Spine and Live2D runtimes are covered by their owners' licenses, not by azl-dl's.

## Running the tests

The test suite renders every page headlessly. Tests that read game data are skipped unless `AZLDL_DATA` points at a data folder:

```bash
dotnet test
```

## Reporting problems

Open an issue at https://github.com/vermilion10/azl-dl/issues with what you did, what you expected and what happened instead. The app links there from **Settings > Help and feedback** and from any page that fails to load.

## Credits

azl-dl builds on open-source libraries and fonts. They are listed with their licenses in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

azl-dl is an unofficial fan project, not affiliated with or endorsed by Manjuu, Yongshi or Yostar. Azur Lane and all game content, including art, audio, text and data, belong to their respective owners. Use the tool for personal viewing and archiving, and respect the game's terms of service.
