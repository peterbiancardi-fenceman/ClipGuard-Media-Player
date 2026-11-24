**Public beta.** Download **ClipGuard Media Player Setup 0.9.7.exe** below and run it (no administrator rights needed). It installs over 0.9.6 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 0.9.7**

- **Lens, Auto:** a film that carries its own active-area metadata is decided at once and exactly (no 4 second wait); other films are still read from the picture.
- **Lens, scope is held:** once a film shows a scope letterbox it stays scope for the rest of the film; taller IMAX scenes are cropped top and bottom to the screen's shape.
- **Lens, 3840 x 1920 encodes** are read for a scope letterbox too.
- **Lens on 2.00 / 2.10 films** (taller but narrower): Standard now uses RAVU-Zoom-AR for the height and SSimDownscaler for the width; Lightweight uses Lanczos for both.
- **Neural 2x first** no longer switches itself off after a pause, a frame step or a seek; choosing it again turns it back on for the film.
- **L** while watching (paused too) cycles Lens upscale: Lightweight, Standard, Neural 2x first.
- **FSRCNNX** keeps the source's brightness exactly (it drifted very slightly darker).
- **Banner (F6):** the scaling path says width / height, and "none (1:1)" when nothing is scaled.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer. RAVU-Zoom-AR by bjin and SSimDownscaler by igv (LGPL v3); JointBilateral and CfL by Artoriuz (MIT).
