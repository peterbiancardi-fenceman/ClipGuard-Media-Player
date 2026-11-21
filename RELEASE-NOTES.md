**Public beta.** Download **ClipGuard Media Player Setup 0.9.2.exe** below and run it (no administrator rights needed). It installs over 0.9.1 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 0.9.2**

- Mouse: point to select, click to open, right-click for Back, the wheel to move. While watching, a move shows the film's bar, a click is Play / Pause. The pointer hides after 3 seconds.
- Covers: a gold resolution badge (4K, 1080p, 720p / SD) with a coloured edge (red, blue, yellow), and an HDR or HLG tag under it.
- Settings > About: version and build, your PC (Windows, processor, memory, graphics card and driver, display mode) and the Plex server. OK copies it all for an issue.
- ClipGuard's banner is a fixed grid: two rows of five columns that line up and don't move while a film plays.
- The banner shows which calibration LUT is on (or off with F5).
- Picture > Display & Output > Output gamma: 2.2, 2.4 or BT.1886, for a projector set to that gamma (not used while a calibration LUT is on).
- Calibration: choose any .ccss file in the Calibration folder.
- Calibration: black and white check screens with instructions before measuring; the meter checks that the brightest greys aren't squeezed together.
- Calibration: colours the projector can't reach keep its own most saturated colour instead of being pulled off hue (blue in particular).
- Calibration: a meter that stops answering no longer freezes the run. It stops with the meter's error, and a LUT is never built from part of the readings.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer.
