<div align="center">

<img src="assets/icon.png" width="96" alt="Teams Captions Saver icon" />

# Teams Captions Saver

**Save Microsoft Teams live captions, with real speaker names, from the Teams desktop app.**

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?logo=windows&logoColor=white)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

</div>

---

Teams can show live captions during a meeting, but they disappear as soon as the meeting ends.
**Teams Captions Saver** runs next to the Teams desktop app, picks up every caption together with the
name of the person speaking, and saves the whole conversation as a transcript you can search, share
or paste into an AI tool for a summary.

You don't need a bot in the meeting, an admin to enable recording, or a cloud service. Everything
stays on your PC.

> **This copy** is based on [mufeezahmad/TeamsCaptionsSaver](https://github.com/mufeezahmad/TeamsCaptionsSaver)
> at commit `e4a5ff5` and adds **meeting audio recording** (Ogg Opus, saved in parts) and **periodic
> transcript saving while the meeting is on**.

## Features

- 🎙️ **Real speaker names.** Each line shows who said it, taken directly from Teams.
- ⚡ **Live view.** Captions appear in the app as people speak and are corrected in place while the sentence is still being finished.
- 🗣️ **One paragraph per speaker.** Teams splits speech into many short captions; the app joins them back together, so each name and time appears once until someone else speaks.
- 📋 **Copy all.** One click copies the whole transcript, ready to paste into an email, notes or an AI tool.
- 🔎 **Filter and search.** Show only the speakers you pick, or only the lines containing a word (Ctrl+F). Copy all and Save then use just what's shown.
- ⭐ **Bookmarks and highlighter.** Star important lines (click ☆ or Ctrl+B) and highlight the exact words that matter. Saved transcripts start with a Highlights list.
- 🤖 **Hands-free.** Detects when you join a meeting, can switch Teams captions on for you, and saves when the meeting ends.
- 📄 **Five export formats:** plain text, Markdown, JSON, CSV (opens in Excel) and WebVTT subtitles.
- 💾 **Crash-safe.** A backup copy is written every 15 seconds, and the transcript files themselves are re-saved every few minutes during the meeting, so a crash or reboot doesn't lose the meeting.
- 🎧 **Audio recording.** Records your microphone and what Teams plays (speaker loopback) as small Ogg Opus files next to the transcript: your voice on the left channel, everyone else on the right. A new file starts every 10 minutes on the clock (14:00, 14:10, …) and is named after its start time, and every transcript line says which file and where in it to listen (`🔊 14-10-00 @ 3:23`). The file being written stays playable even after a crash.
- 📁 **Organised by meeting.** Each meeting gets a folder, with a folder per day inside: `Weekly sync\2026-11-18\`. The transcript and its audio sit together.
- 📞 **Several calls at once.** When you join a meeting while another waits on hold, the first meeting is saved and finished under its own name and a new transcript starts.
- ✏️ **Rename speakers.** Right-click to fix a name (for example a meeting-room account). The new name is remembered for future meetings.
- 🔒 **Private.** No network access, no admin rights. Audio is recorded only when that setting is on.

## How it works

The new Teams client (`ms-teams.exe`) draws its interface with Microsoft Edge WebView2. WebView2 exposes
everything on screen through **Windows UI Automation**, the same accessibility API that screen readers
use. Teams Captions Saver reads the live-captions panel through that API about once a second.

```mermaid
flowchart LR
    A[Meeting audio] --> B[Teams live captions]
    B -->|UI Automation| C[Caption reader]
    C --> D[Transcript<br/>speaker + text + time]
    D --> E[Live view]
    D --> F[TXT · MD · JSON · CSV · VTT]
    D --> G[Backup every 15 s]
```

Teams does the speech-to-text and knows who is talking, which is why the names are accurate. This app
records what Teams shows and keeps it.

## Getting started

### Requirements

- Windows 10 or 11
- The **new** Microsoft Teams desktop app
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), or use the self-contained build below

### Run it

1. Start **TeamsCaptionsSaver.exe**.
2. Join a meeting in the Teams desktop app.
3. Captions are switched on for you automatically. To do it yourself, go to **More (…) → Language and
   speech → Show live captions**.
4. The status turns green (**Capturing**) and the transcript fills in as people talk.
5. When the meeting ends, the transcript is saved to `Documents\Teams Transcripts`. You can also
   click **Save as…** at any time.

> **Tip:** The Teams window can sit behind other windows, but keep the captions switched on. If they're
> off, there is nothing for the app to record.

### Example output

```text
Meeting: Weekly sync
Date: Wednesday, October 14, 2026
Time: 10:00 - 10:32
Speakers: Alex Johnson, Sam Lee

[10:00:12] Alex Johnson: Morning everyone, let's start with the release checklist.
[10:00:19] Sam Lee: Sounds good. The build passed overnight, so we're ready for QA.
[10:00:31] Alex Johnson: Great. Any blockers before Friday?
```

## Settings

| Setting | What it does |
|---|---|
| **Save to** | Folder for transcripts (default `Documents\Teams Transcripts`) |
| **File name** | Pattern made from `{date}`, `{time}` and `{title}`, e.g. `2026-10-14_10-00_Weekly sync.txt` |
| **Subfolders** | Folders inside the save folder, separated by `/` (default `{title}/{date}`, e.g. `Weekly sync\2026-10-14\`). Leave empty to save everything in one folder |
| **Formats** | Which formats to write when saving: TXT, MD, JSON, CSV, VTT |
| **Auto-save when the meeting ends** | Saves as soon as you leave the meeting |
| **Turn on Teams live captions automatically** | Switches on "Show live captions" when a meeting starts |
| **Auto-scroll** | Keeps the newest caption in view |
| **During the meeting, save the transcript** | Re-saves the transcript files every 1–10 minutes while the meeting is on (default 2), overwriting the same files |
| **Record meeting audio** | Records the meeting next to the transcript as `<file name>_audio_14-03-07.ogg`, `_audio_14-10-00.ogg`, …: each file is named after the time it starts |
| **Audio quality** | Stereo (me left, others right) at 48 or 64 kbps, or everyone mixed in mono at 16 or 24 kbps. Opus merges the two sides of a stereo file below ~44 kbps, so stereo starts at 48 kbps |
| **New file every** | Length of each audio part (5–60 minutes, default 10). Parts start on round times, so the first one is shorter |
| **Me (microphone) / Others (speaker output)** | Devices to record. The defaults follow Windows' default and default communications devices, and recording switches over when a headset is plugged in mid-meeting |

**Finding a line in the audio.** The audio runs in step with the clock, so a line said at 14:13:25 is
3:25 into the file whose name ends `14-10-00`. Saved transcripts spell it out: each line in TXT ends with
`🔊 14-10-00 @ 3:23`, Markdown shows it after the speaker, and JSON and CSV have the file name and position.
The position starts 2 seconds early because Teams shows a caption a moment after the words are spoken.

Audio sizes per hour: about 21 MB (stereo 48 kbps), 11 MB (mono 24 kbps), 7 MB (mono 16 kbps).
Ogg Opus plays in VLC and the Windows Media Player app, and ffmpeg and Whisper read it directly. The
microphone is recorded even while you're muted in Teams; pause capture if you need it off.

## Build from source

```bash
git clone https://github.com/mufeezahmad/TeamsCaptionsSaver.git
cd TeamsCaptionsSaver
dotnet build TeamsCaptionsSaver.sln
dotnet test TeamsCaptionsSaver.sln
```

Publish a single `.exe`:

```bash
dotnet publish TeamsCaptionsSaver.Desktop -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

To get an `.exe` that runs without .NET installed, use `--self-contained true` instead. The file is
about 150 MB.

To try the app without a meeting, start it with `--demo`. It shows a few made-up captions and doesn't
connect to Teams:

```bash
publish/TeamsCaptionsSaver.exe --demo
```

## Troubleshooting

<details>
<summary><b>Captions are showing in Teams, but the app says they're off</b></summary>

Microsoft may have changed how Teams lays out its captions. While captions are on screen, click
**Diagnostics**. The app writes what it can see in the Teams window to a text file; the line
`captions region:` says whether it found the captions. The matching rules are in
`Capture/CaptionParser.cs` and `TeamsCaptionReader.FindCaptionsContainer`.

The dump contains whatever is on screen in Teams (names, chat), so check it before sharing it.
</details>

<details>
<summary><b>Which versions of Teams are supported?</b></summary>

Only the **new** Teams desktop app (`ms-teams.exe`). Classic Teams and Teams in a browser are not
supported.
</details>

<details>
<summary><b>Where are logs and backups?</b></summary>

`%LOCALAPPDATA%\TeamsCaptionsSaver\`: `log.txt`, `settings.json`, and `backups\` (backups are kept
for 30 days).
</details>

## Project structure

```text
TeamsCaptionsSaver.Desktop/
├── Capture/
│   ├── TeamsCaptionReader.cs     Finds Teams windows, the meeting and the captions; turns captions on
│   ├── MeetingSelector.cs        Picks the meeting to follow when one is on hold
│   ├── CaptionParser.cs          Turns a UI snapshot into (speaker, text) pairs
│   ├── UiNode.cs                 Snapshot of one UI Automation element
│   └── Native.cs                 Win32 window enumeration
├── Audio/
│   ├── MeetingAudioRecorder.cs   Recorder thread: devices, wall-clock pacing, file parts
│   ├── CaptureSource.cs          One WASAPI microphone or loopback capture, converted to 48 kHz mono
│   ├── AudioMixer.cs             Keeps sources in sync (silence fill, clock drift) and mixes them
│   ├── OpusChunkWriter.cs        Ogg Opus encoder, files named after their start time
│   └── AudioDevices.cs           Device choices for the settings
├── Transcript/
│   ├── TranscriptSession.cs      Merges each scan into the transcript, without duplicates
│   └── TranscriptExporter.cs     TXT / MD / JSON / CSV / VTT
├── Models/                       Caption entry and settings
└── MainWindow.xaml(.cs)          UI, auto-save, backups and settings
TeamsCaptionsSaver.Tests/         Unit tests for parsing, merging, exports and audio
```

## Privacy

Live captions and audio can contain sensitive conversation. Transcripts and recordings are stored only
on your PC, in the folder you choose (if that folder syncs to OneDrive, so do they). The app never sends
anything over the network. Follow your organisation's policies and the law where you are, and tell the
other participants when you're saving a meeting transcript or recording.

## License

[MIT](LICENSE) © 2026 Mufeez Ahmad

Audio uses [NAudio](https://github.com/naudio/NAudio) and [Concentus](https://github.com/lostromb/concentus) (both MIT).
