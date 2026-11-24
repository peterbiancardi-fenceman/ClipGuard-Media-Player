**Public beta.** Download **ClipGuard Media Player Setup 0.9.6.exe** below and run it (no administrator rights needed). It installs over 0.9.5 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 0.9.6**

- **RAVU-Zoom-AR** upscales every picture that isn't about 2x (FSRCNNX still takes 2x), the lens's taller-only stretch included: sharper edges than Jinc2 or Lanczos, with no ringing. On by default (Settings > Picture > Scaling).
- **SSimDownscaler**, a new Downscaling choice: keeps the fine detail and contrast a plain downscale softens.
- **JointBilateral and CfL chroma upsampling** (Chroma upsampling): the full-size brightness picture guides the colour, for sharper colour edges with no ringing.
- **ClipBit**: brings back fine texture lost to a low bitrate (Detail, 80) and adds fine film grain (Grain, 50), on 4K films only. On by itself for films under 25 Mbps; Off, Under 15 / 20 / 25 / 30 Mbps or Always in Settings > Picture > Enhancement.
- **Lens upscale** (anamorphic lens): Lightweight (Lanczos3), Standard (RAVU-Zoom-AR) or Neural 2x first (the letterbox cut off, the picture doubled by FSRCNNX, then down to size by SSimDownscaler). Neural 2x first needs a powerful GPU; if frames keep being skipped it switches itself off for that film.
- **Lens, Auto**: 2.00 and 2.10 films now fill the screen's height at their own shape with their letterbox cut off (before, they kept the 1.78 frame with their bars).
- **Screen geometry** now also applies to the library, menus and playback bar.
- **Refresh rate card**: near the bottom of the picture, "Changing to 23.976 Hz" while the display re-locks, then the rate for a moment.
- **Colorimetry flag** follows Output gamut: the display is told BT.2020 only when BT.2020 is sent (BT.709 otherwise), on NVIDIA cards.
- **Banner (F6)**: GPU %, CPU % and render time against the film's frame time (yellow when it's tight), and the scaling path (sizes and each step, e.g. "FSRCNNX 2x > Catmull-Rom"). The frame-rate badge shows the film's own rate (it no longer jumps between 24p and 23.96p).

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer. RAVU-Zoom-AR by bjin and SSimDownscaler by igv (LGPL v3); JointBilateral and CfL by Artoriuz (MIT).
