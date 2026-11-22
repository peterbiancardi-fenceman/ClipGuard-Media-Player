**Public beta.** Download **ClipGuard Media Player Setup 0.9.4.exe** below and run it (no administrator rights needed). It installs over 0.9.3 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 0.9.4**

- Faster screens and remote: covers, photos and backdrops are drawn from copies made once at their 4K size, text fonts are reused, and the playback bar redraws less often. Nothing looks different.
- Calibration: a White point choice. The default is the Dangbei White Point (x 0.3034, y 0.3061, CIE 1931 2°), which should be perceptually correct for triple laser projectors, set with a live R / G / B meter beside the white window (the brightness under it, ON TARGET when all three are within 1%). Or As set (by eye), or set by the LUT for a projector mode whose own controls can't reach it; the results say how much of the projector's white is kept.
- Calibration opened from the player takes only its own keys (L, N, C, Esc), and a stopped run goes back to the setup.
- While paused: ClipGuard's banner (F6) and menu (F7) now show and update, and the bottom bar hides after 3 seconds (any key brings it back).

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer.
