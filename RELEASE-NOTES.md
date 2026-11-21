**Public beta.** Download **ClipGuard Media Player Setup 0.9.1.exe** below and run it (no administrator rights needed). It installs over 0.9.0 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys and the remote.

**New in 0.9.1**

- Power tab: Close ClipGuard Media Player, Restart ClipGuard Media Player, Shut down the HTPC.
- Esc and Back only go back a screen; they never close the player (Alt+F4 or Power > Close does).
- Settings > Start with Windows: opens the player when you sign in.
- Settings > Theme: Midnight, Cinema, Ocean, Graphite or High Contrast.
- Settings > Menu sounds: Off, Soft or Normal (library and menus only).
- Movies and TV Shows: OK on the tab opens Sort by (title, date added, release year, running time) and Show (all, or unwatched only).
- Sound / Subtitles menu: Up from the first line reaches the Sound / Subtitles tabs; Left / Right switches between them.
- During a film, Up and Down no longer skip a minute.
- Learn remote: new Video Processor, Media Player and Banner buttons (Ctrl+V and Ctrl+M on a keyboard). A button skipped while learning keeps what it had.
- A remote's Home button shows or hides ClipGuard's banner (F6) while the player is in front, and it stays up until pressed again.
- Top banner: the text below the top row is 25% bigger.
- Home screen: the rows no longer run into the help line.
- Video Processor: Info is smaller, in the corner, and only on I; "Waiting for signal" is a small card after 3 seconds.
- Fixed a crash when switching to Video Processor.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer.
