**ClipGuard Media Player 1.1.0.** Download **ClipGuard Media Player Setup 1.1.0.exe** below and run it (no administrator rights needed). It installs over 1.0.0 and keeps your settings. See the [README](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player#readme) for requirements, setup, keys, the remote and the mouse.

**New in 1.1.0**

- **Low latency (Video Processor):** captured frames are shown as soon as they are ready instead of on a schedule set when the capture starts. On a 4K 23.976 capture the time inside the PC went from about 150 ms to about 35 ms, so the picture is much less behind the sound; no frame is dropped. On by default; Settings > Low latency turns it off. If you had set an audio delay on your receiver to match the old timing, lower it.
- **Latency readout:** in Video Processor the info banner (F6) shows the capture-to-display time in the PC and where it goes (capture, queue, wait, draw).
- **GPU temperature:** the Info card (I) and the info banner show the NVIDIA graphics card's temperature and fan speed (read-only).
- **F7 menu:** the LUT row is gone; the calibration LUT is chosen in Settings > Picture > Calibration LUT, as before (in the player that row did nothing).
- **Info banner:** an SDR signal's brightness curve shows as "Transfer SDR" instead of "Transfer BT.709", which looked like a colour label.

Video Processor needs an unencrypted HDMI signal.

Please report problems as [issues](https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues), with what Settings > About copies and `crash.log` from `%LOCALAPPDATA%\ClipGuard\MediaPlayer` if there is one.

Built from:
- engine: [unplugged-mpc-hc, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-mpc-hc/tree/clipguard-media-player)
- renderer: [unplugged-VideoRenderer, branch clipguard-media-player](https://github.com/peterbiancardi-fenceman/unplugged-VideoRenderer/tree/clipguard-media-player)

Built on Unplugged's open-source engine and renderer (github.com/spedagadi), based on MPC-HC and MPC Video Renderer. RAVU-Zoom-AR by bjin and SSimDownscaler by igv (LGPL v3); JointBilateral and CfL by Artoriuz (MIT).
