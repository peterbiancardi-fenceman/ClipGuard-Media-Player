**Public beta.** Download **ClipGuard Media Player Setup 0.9.5.exe** below and run it (no administrator rights needed). It installs over 0.9.4 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 0.9.5**

- Anamorphic lens support (Settings > Picture > Anamorphic lens: Off, 1.25x, 1.33x or 1.5x, and Scope screen: 2.35 or 2.40). Each film is decided once, with no switching while it plays: a scope film (2.2 or wider) fills the screen with its letterbox cut off; any other film fills the screen's height with black at the sides, squeezed for the lens.
- Film shape: Auto reads it from the picture (a scope letterbox in the film's first few seconds of picture makes it scope for the whole film; credits and text are ignored), or set it to Scope or Not scope. This works for HDR and SDR.
- With a lens, the library, menus and playback bar are squeezed too, so they come out the right shape through it.
- Define screen: 12-point screen geometry on the screen's edges (4 along the top, 4 along the bottom, 2 on each side). Arrows move a point, OK goes to the next, Back saves, Stop undoes. Each edge is a smooth curve through its points, and the picture is redrawn with EWA Lanczos (jinc) with anti-ringing. Screen geometry On / Off and Reset geometry are beside it. Saved as Screen.screen in the Calibration folder and loaded at every start.
- ClipGuard's banner (F6) shows the lens, the screen, the film's shape and how it was decided, its measured aspect, the scaling, and whether screen geometry is on.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer.
