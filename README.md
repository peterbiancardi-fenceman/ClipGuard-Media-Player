# ClipGuard Media Player

A home theater player for Windows, made for projectors with dynamic laser dimming.
It plays films and TV shows from your Plex server, and it can show a live HDMI source
through a capture card, with the same picture processing.

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
- **ClipBee.** A picture enhancement that adds snap and depth: texture, outlines and
  the separation of foreground from background, on brightness only, so colours stay
  as they are. One setting, 0 to 100 (off by default, 25 recommended). Grain in flat
  areas, near-black and highlights are left alone, and it adds no delay. While
  watching, Down opens it and Left / Right change it.
- **Projector calibration.** It builds a 3D LUT for any projector with a supported
  meter, with or without a colorimeter correction file (any .ccss in the Calibration
  folder). Black and white check screens come first, and colours the projector can't
  reach keep its own most saturated colour instead of shifting hue. The white and
  black it measures set the display levels for tone mapping when its LUT is in use.
  White point: the default is the **Dangbei White Point** (x 0.3034, y 0.3061, CIE 1931
  2°), which should be perceptually correct for triple laser projectors. A live R / G / B
  meter beside the white window shows how far each is from it while you set the
  projector's gains. Two other choices: As set (the white you set by eye), or set by the
  LUT, for a projector mode whose own controls can't reach it (the LUT turns the
  channels down, and the results say how much of the projector's white is kept).
- **Anamorphic lens and screen geometry.** With a lens set (1.25x, 1.33x or 1.5x)
  and a 2.35 or 2.40 screen, a scope film fills the screen with its letterbox cut off,
  and any other film fills the screen's height at its own shape (a 2.00 or 2.10
  letterbox is cut off too) with black at the sides. Each film is decided from its own
  active-area metadata when it has it (at once), otherwise from the picture (credits
  ignored), or set by hand. Once a film is scope it stays scope (taller IMAX scenes are
  cropped top and bottom). Lens
  upscale: Lightweight (Lanczos3), Standard (RAVU-Zoom-AR) or Neural 2x first (FSRCNNX,
  then SSimDownscaler; a powerful GPU with at least 8 GB of video memory is required). Define screen sets 12 points on the
  screen's edges; the picture is redrawn to fit them with EWA Lanczos, menus included.
  All are in Settings > Picture.
- **Scaling and detail.** RAVU-Zoom-AR upscales anything that isn't about 2x (FSRCNNX
  takes 2x). SSimDownscaler is a Downscaling choice. JointBilateral and CfL chroma
  upsampling. ClipBit brings back fine texture and adds fine grain on low-bitrate 4K
  films (on by itself under 25 Mbps; Settings > Picture > Enhancement).
- **Video Processor.** It shows a capture card's HDMI input full screen, with
  ClipGuard and the picture settings applied. It needs an unencrypted HDMI signal (see
  below).
- **Remote control and mouse.** It learns your remote's buttons (see [Remote control](#remote-control)),
  and works with a mouse (see [Mouse](#mouse)).
- **Covers.** A gold resolution badge (4K, 1080p, 720p) and an HDR or HLG tag on each cover.
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
| Down | ClipBee, while watching (Left / Right change it, Down closes it) |
| L | Lens upscale, while watching: Lightweight / Standard / Neural 2x first (works paused too) |
| I | The film's bar (in a film); Info (in Video Processor) |
| Alt+F4 | Close the player (or Power > Close ClipGuard Media Player) |

On Movies and TV Shows, OK on the tab opens Sort by (title, date added, release year,
running time) and Show (all, or unwatched only); each remembers its own choice.

Settings > About shows the version and build, your PC (Windows, processor, memory,
graphics card and driver, display mode) and the Plex server; OK copies it to the
clipboard for an issue.

## Mouse

- Pointing at a tab, cover, row or button selects it; a click opens it (a setting with
  a value steps to the next one).
- Right-click is Back. The wheel moves up and down (Shift + wheel moves along a row of
  covers).
- While watching, moving the mouse shows the film's bar, a click is Play / Pause and a
  right-click is Back.
- The pointer hides after 3 seconds without moving.

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
happened. Paste what Settings > About copies, and attach `crash.log` from
`%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one. Don't attach screenshots of copy-protected content.

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
- RAVU-Zoom-AR by bjin (LGPL v3)
- SSimDownscaler by igv (LGPL v3)
- JointBilateral and CfL chroma upsampling by Artoriuz (MIT)
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
