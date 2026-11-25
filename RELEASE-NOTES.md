**ClipGuard Media Player 1.0.0.** Download **ClipGuard Media Player Setup 1.0.0.exe** below and run it (no administrator rights needed). It installs over the 0.9 betas and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 1.0.0**

- **Themes:** the info banner (F6), the in-film settings panel and the ClipBee bar now follow the theme chosen in Settings > Theme, and change with it straight away. Warning chips and warning lines keep their own colour in every theme so they still stand out.
- **F11 log:** saves the log and opens its folder, with no upload code left in the player or the meter.
- **Clean-up:** code the public player never used was removed (network search for other devices, unused settings and fields); Plex is told the player's real version.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer. RAVU-Zoom-AR by bjin and SSimDownscaler by igv (LGPL v3); JointBilateral and CfL by Artoriuz (MIT).
