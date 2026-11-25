ClipGuard Media Player
======================

A home theater player for Windows, made for projectors with dynamic laser dimming:
films and TV shows from your Plex server, and a capture card's HDMI input (Video
Processor), with ClipGuard and the picture settings.

The full guide, updates and problem reports:
https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player

Keys: Arrows / Enter move and select, Esc or Backspace back, F4 Video Processor (in
the library) or back to the library (while watching), F6 ClipGuard's banner, F7
ClipGuard menu, F5 calibration LUT off / on, A sound and subtitles, I the film's bar
or info, Down ClipBee while watching (Left / Right change it), Alt+F4 (or Power >
Close) close the player.

Mouse: point to select, click to open, right-click back, the wheel moves up and down.
While watching, a click is Play / Pause and a right-click is back.

Video Processor needs an unencrypted HDMI signal. ClipGuard Media Player does not
decrypt, remove or bypass HDCP or any other copy protection, and it cannot record or
save the video it shows.

Settings and logs: %LOCALAPPDATA%\ClipGuard. If the player closes or misbehaves,
crash.log in %LOCALAPPDATA%\ClipGuard\MediaPlayer has the details: attach it to an
issue on GitHub, with what Settings > About copies.

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based
on MPC-HC and MPC Video Renderer. Licences: LICENSE.txt and the licenses folder.
No telemetry. No warranty (GPL v3).
