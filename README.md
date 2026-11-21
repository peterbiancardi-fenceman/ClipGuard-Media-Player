# ClipGuard Media Player (beta)

A home theater player for Windows, made for projectors with dynamic laser dimming.
It plays films and TV shows from your Plex server, and it can show a live HDMI source
through a capture card, with the same picture processing.

> **Beta.** This is a public beta. Expect rough edges, and please report problems
> (see [Reporting problems](#reporting-problems)).

## What it does

- **ClipGuard.** It protects highlights on projectors whose laser dims in dark
  scenes. It looks ahead a few frames and keeps bright details from clipping when the
  laser is dimmed.
- **Plex library.** Movies and TV shows from your Plex server: Continue Watching, Next Up, Recently Added Movies and TV,
  seasons and episodes, next episode, resume, watched status, sound and subtitle
  tracks.
- **Bitstream audio.** Lossless and surround tracks go untouched to your AV receiver
  or processor.
- **Frame rate matching.** The display's refresh rate follows the film (23.976,
  24, 50, 59.94 Hz and so on).
- **HDR to SDR tone mapping.** For projectors that look best with SDR, with picture
  settings organised in sections.
- **Projector calibration.** It builds a 3D LUT for any projector with a supported
  meter, with or without a colorimeter correction file.
- **Video Processor.** It shows a capture card's HDMI input full screen, with
  ClipGuard and the picture settings applied. It needs an unencrypted HDMI signal (see
  below).
- **Remote control.** It learns your remote's buttons (see [Remote control](#remote-control)).
- **Made for a home theater PC.** A Power tab (close or restart the player, shut down
  the HTPC), Start with Windows, five dark colour themes, soft menu sounds, and Sort /
  Show (unwatched only) for Movies and TV Shows.

## Requirements

- Windows 10 or 11, 64-bit.
- A graphics card with DirectX 11 (a recent NVIDIA, AMD or Intel GPU).
- A Plex Media Server for the library.
- For Video Processor: a Blackmagic DeckLink capture card (tested with the DeckLink
  Mini Recorder 4K) and Blackmagic Desktop Video, the card's driver, from Blackmagic
  Design's website.
- For calibration: a meter supported by ArgyllCMS.
- Nothing else: the decoders, the renderer and the calibration tools come with the
  player.

## Install

1. Download **ClipGuard Media Player Setup <version>.exe** from
   [Releases](../../releases).
2. Run it. It installs for your Windows user (no administrator rights needed), adds a
   Start menu entry and, if you want, a desktop shortcut.
3. Start it and sign in to Plex with the code shown on screen.

To update, download the newest Setup from Releases and run it: it installs over the old
version and keeps your settings. To remove it, use Windows Settings > Apps.

Settings and logs are kept in `%LOCALAPPDATA%\ClipGuard`.

## Keys

| Key | Action |
| --- | --- |
| Arrows, Enter | Move, select |
| Esc, Backspace / Back | Back (it never closes the player) |
| F4 | Video Processor (in the library); back to the library (while watching) |
| Ctrl+V / Ctrl+M | Video Processor / back to the Media Player, from anywhere |
| F6 | ClipGuard's banner at the top, on / off |
| F7 | ClipGuard menu |
| F5 | Calibration LUT off / on |
| A | Sound and subtitles |
| I | The film's bar (in a film); Info (in Video Processor) |
| Alt+F4 | Close the player (or Power > Close ClipGuard Media Player) |

On Movies and TV Shows, OK on the tab opens Sort by (title, date added, release year,
running time) and Show (all, or unwatched only); each remembers its own choice.

## Remote control

The player works with a remote whose buttons reach Windows as keys or media buttons,
through a USB receiver or Bluetooth. (Remotes made for one device, such as an Apple TV
or Fire TV remote, usually don't send their buttons to Windows.) To set it up:

1. Go to **Settings > Learn remote** and press Enter.
2. The player asks for each button in turn: Up, Down, Left, Right, OK, Back,
   Play / Pause, Stop, Next chapter, Previous chapter, Skip forward, Skip back, Menu,
   Video Processor, Media Player and Banner. Press that button on your remote.
3. If your remote doesn't have a button, wait 8 seconds and the player moves on (a
   button you skip keeps what it had).

The row shows how many buttons have been learned. Run it again at any time to change
them. The keyboard keys keep working alongside the remote. While watching, Menu opens
the sound and subtitle menu (Up from its first line reaches the Sound / Subtitles tabs).
A remote's Home button shows or hides ClipGuard's banner, like F6, while the player is
in front.

## Video Processor and copy protection

**Video Processor needs an unencrypted HDMI signal.**

ClipGuard Media Player does not decrypt, remove or bypass HDCP or any other copy
protection. It cannot record or save the video it shows; it only displays it. You are
responsible for using it with sources and content you are permitted to use, and in
line with the laws where you live and the terms of your devices and services.

Sources that give an unencrypted signal include game consoles with HDCP turned off in
their own settings (for example PlayStation 5: Settings > System > HDMI > Enable
HDCP; while it is off, the console's video apps and disc playback do not play), PCs,
cameras and test pattern generators.

**Gaming:** set ClipGuard > Look-ahead (Video Processor) to Off. Every frame of
look-ahead delays the picture by one frame (about 17 ms at 60 Hz).

**Sound with Video Processor:**
- *Through the capture card (games):* when the source sends PCM (on a PlayStation 5,
  Settings > Sound > Audio Output > Audio Format (Priority) > Linear PCM), the player
  plays it on this PC's default sound device, delayed to match the picture. Fine-tune
  it with Settings > Game audio delay.
- *Straight to your receiver:* for Dolby, DTS and lossless sound, connect the source's
  audio to your receiver and set that input's audio delay there to match the picture.

## Reporting problems

Open an [issue](../../issues) on this repository with what you did, what you expected, and what
happened. Attach `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is
one. Don't attach screenshots of copy-protected content.

## Privacy

No telemetry. Your Plex sign-in, settings and logs stay on your PC.

## Credits

Built on Unplugged's open-source engine and renderer
([github.com/spedagadi](https://github.com/spedagadi)), based on MPC-HC and MPC Video
Renderer.

It also uses:
- LAV Filters (GPL)
- MPC-BE's MPC Audio Renderer (GPL v3)
- ArgyllCMS (AGPL v3)
- ONNX Runtime (MIT)
- FSRCNNX (LGPL)
- Ultralytics YOLO models (AGPL v3)

Full notices are in [licenses/THIRD-PARTY-NOTICES.txt](licenses/THIRD-PARTY-NOTICES.txt) and
in the `licenses` folder of every install.

## Licence

GPL v3; see [LICENSE](LICENSE). The source:
- the player: this repository (each release is tagged)
- the engine: [peterbiancardi-fenceman/unplugged-mpc-hc](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player), branch `clipguard-media-player`
- the renderer: [peterbiancardi-fenceman/unplugged-VideoRenderer](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player), branch `clipguard-media-player`

Building: `build\build.ps1` on Windows with Visual Studio 2022 (it fetches the rest), or
the GitHub Actions workflow in this repository.

## Trademarks

Plex, Dolby, DTS, Blackmagic Design, DeckLink, PlayStation, NVIDIA, AMD, Intel,
Windows and the names of projectors are trademarks of their owners. They are named
here only to describe what the player works with. This software is not made,
endorsed or supported by them.

## No warranty

This software is provided as is, without warranty of any kind. See the GPL v3
licence.
