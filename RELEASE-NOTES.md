**Public beta.** Download **ClipGuard Media Player Setup 0.9.3.exe** below and run it (no administrator rights needed). It installs over 0.9.2 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 0.9.3**

- ClipBee: a picture enhancement that adds snap and depth (texture, outlines, and the separation of foreground from background), on brightness only, so colours stay as they are. One setting, 0 to 100: off by default, 25 recommended. Grain in flat areas, near-black and highlights are left alone, and it adds no delay. While watching, Down opens it and Left / Right change it; it is also in Picture > Enhancement. With ClipBee on, micro contrast is turned off.
- Calibration: the white and black the meter measures are saved with the LUT, and Picture > Calibration LUT > Display levels (From LUT, the default) sets the display levels for tone mapping from them.
- A film with more than one version opens and resumes on the version played last.
- Continue Watching and Recently Added show the HDR / HLG tags too.
- ClipGuard's banner is back to two rows: File, Source, HDR, Output and ClipGuard. Chroma upscaling is under Source, skipped frames under Output.
- The banner shows Full or Limited range for the source and for the graphics card's output (NVIDIA).
- Chapter skip moves one chapter per press.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer.
