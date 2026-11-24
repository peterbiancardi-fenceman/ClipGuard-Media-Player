// ClipGuard+ Player (test) — the Media Player as its own program (2.56.0).
//
// Plays Plex films in its own window through our build of Unplugged's engine
// (s3engine.dll in this folder: Unplugged's engine, GPL v3, with Plex streaming
// added) and the ClipGuard renderer. Unplugged itself is not used.
//
// Lives in the Player folder of the ClipGuard Test package. On each start it copies
// the decoders and the renderer (filters folder) from ClipGuard's own copy of
// Unplugged (%LOCALAPPDATA%\ClipGuard\Unplugged), so ClipGuard must have run once.
// The Plex sign-in is shared with ClipGuard (clipguard.ini one folder up), and so are
// ClipGuard's settings: ClipGuard runs inside the player (F7 menu, keys, info banner).
#if PLAYER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClipGuard
{
    static class PlayerProgram
    {
#if STANDALONE && PUBLIC
        const string Title = "ClipGuard Media Player";
        public const string MediaPlayerVersion = "0.9.6";   // the public beta's own numbering
#elif STANDALONE
        const string Title = "ClipGuard Media Player";
        public const string MediaPlayerVersion = "2.95.1";
#else
        const string Title = "ClipGuard+ Player (test)";
#endif

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        const int RestartUpdated = 2;

        // 2.70.0: anything that goes wrong is written to crash.log (in the player's settings
        // folder) with the full error, instead of a Windows error box or a silent close.
        public static string CrashLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if STANDALONE
            "ClipGuard", "MediaPlayer", "crash.log");
#else
            "ClipGuard", "player-crash.log");
#endif

        public static void LogCrash(string what, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLog));
                FileInfo fi = new FileInfo(CrashLog);
                if (fi.Exists && fi.Length > 2000000) File.Delete(CrashLog);
                File.AppendAllText(CrashLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + Title
#if STANDALONE
                    + " " + MediaPlayerVersion
#endif
                    + "  " + what + "\r\n" + (ex == null ? "" : ex.ToString() + "\r\n") + "\r\n");
            }
            catch (Exception) { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // An error in the user interface is logged and the player carries on; one on another
            // thread ends the program, so it is logged first.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { LogCrash("error (the player carried on)", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.IsTerminating ? "fatal error (the player closed)" : "error", e.ExceptionObject as Exception);
            };
            bool updated = Array.IndexOf(args, "/updated") >= 0;
            int rc;
            bool created;
            // A restart after an update waits for this one to let go of the mutex.
            for (int i = 0; ; i++)
            {
                using (System.Threading.Mutex one = new System.Threading.Mutex(true, "ClipGuardPlusPlayerTest", out created))
                {
                    if (!created)
                    {
                        if (updated && i < 50) { System.Threading.Thread.Sleep(200); continue; }
                        return 0;
                    }
                    try { rc = Run(updated); }
                    catch (Exception ex)
                    {
                        LogCrash("could not start", ex);
                        MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                }
                break;
            }
            if (rc == RestartUpdated)
            {
                // 2.56.0: a newer build was installed: start the new player.
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "/updated");
                    psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                return 0;
            }
            return rc;
        }

#if STANDALONE
        // 2.67.0: ClipGuard Media Player, installed on its own (ClipGuard Media Player Setup.exe):
        // everything it needs is in its own folder (engine, decoders, DeckLink filter, renderer,
        // ML models, ArgyllCMS, the meter); its settings are in %LOCALAPPDATA%\ClipGuard\MediaPlayer.
        // Neither Unplugged nor ClipGuard.exe is used. The first start brings the Test build's
        // settings over (Plex sign-in, ClipGuard settings, remote, player delay, LUTs).
        static int Run(bool updated)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dataDir = Path.Combine(local, "ClipGuard", "MediaPlayer");
            Directory.CreateDirectory(dataDir);
            string seedIni = Path.Combine(dataDir, "clipguard.ini");
            string liveIni = Path.Combine(dataDir, "clipguard-settings.ini");
            if (!File.Exists(Path.Combine(exeDir, "s3engine.dll")))
                throw new Exception("s3engine.dll is missing from " + exeDir + ". Install ClipGuard Media Player again.");

            if (Process.GetProcessesByName("Unplugged").Length > 0 || Process.GetProcessesByName("ClipGuard").Length > 0
                || Process.GetProcessesByName("ClipGuard+ Player").Length > 0)
            {
                MessageBox.Show("Close ClipGuard, the ClipGuard+ Player and Unplugged first, then start ClipGuard Media Player again.", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            Migrate(seedIni, liveIni);

            Updater.Root = "ClipGuard-MediaPlayer";
            Updater.Nested = true;
            Updater.Required = new[] { "ClipGuard Media Player.exe", "filters\\unplugged_vr.ax" };
            Updater.DeleteOld();
            if (!updated)
            {
                Cursor.Current = Cursors.WaitCursor;
                bool installed = Updater.Run(exeDir, seedIni);
                Cursor.Current = Cursors.Default;
                if (installed) return RestartUpdated;
            }

            ControlForm.PlayerMode = true;
            ControlForm cg = new ControlForm(liveIni, seedIni);
            cg.OpenPlayer(exeDir, dataDir);
            Application.Run(cg);
            return 0;
        }

        // The first start: the Test build's settings, ClipGuard's settings and the LUTs.
        static void Migrate(string seedIni, string liveIni)
        {
            // Public build: a fresh start, nothing brought over from the test builds.
            try
            {
                if (!File.Exists(seedIni)) File.WriteAllText(seedIni, "[ClipGuard]\r\n[MediaPlayer]\r\n");
                if (!File.Exists(liveIni)) File.Copy(seedIni, liveIni);
                Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Calibration"));
            }
            catch (Exception) { }
            return;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try
            {
                if (!File.Exists(seedIni))
                {
                    string test = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ClipGuard-Test", "clipguard.ini");
                    if (File.Exists(test)) File.Copy(test, seedIni);
                    else File.WriteAllText(seedIni, "[ClipGuard]\r\n[MediaPlayer]\r\n[GitHub]\r\nToken=\r\n");
                }
            }
            catch (Exception) { }
            try
            {
                if (!File.Exists(liveIni))
                {
                    string cg = Path.Combine(local, "ClipGuard", "Unplugged", "filters", "clipguard.ini");
                    if (File.Exists(cg)) File.Copy(cg, liveIni);
                    else File.Copy(seedIni, liveIni);
                }
            }
            catch (Exception) { }
            // ClipGuard Meter's Titan / Valerion LUTs into the Calibration folder.
            try
            {
                string cal = Path.Combine(local, "ClipGuard", "Calibration");
                Directory.CreateDirectory(cal);
                string test = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ClipGuard-Test");
                foreach (string dir in new[] { Path.Combine(local, "ClipGuard", "Unplugged", "filters"), test })
                    foreach (string n in new[] { "titan.cube", "titan.txt", "valerion.cube", "valerion.txt" })
                    {
                        string f = Path.Combine(dir, n), d = Path.Combine(cal, n);
                        if (File.Exists(f) && !File.Exists(d)) File.Copy(f, d);
                    }
            }
            catch (Exception) { }
        }
#else
        static int Run(bool updated)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string appDir = Path.GetDirectoryName(exeDir);
            string seedIni = Path.Combine(appDir, "clipguard.ini");
            if (!File.Exists(Path.Combine(exeDir, "s3engine.dll")))
                throw new Exception("s3engine.dll is missing from the Player folder.");

            if (Process.GetProcessesByName("Unplugged").Length > 0 || Process.GetProcessesByName("ClipGuard").Length > 0)
            {
                MessageBox.Show("Close ClipGuard (and Unplugged) first, then start the player again.", Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            // 2.56.0: the player updates itself the way ClipGuard does (Test package,
            // token in clipguard.ini next to ClipGuard.exe), before the engine is loaded.
            Updater.DeleteOld();
            if (!updated)
            {
                Cursor.Current = Cursors.WaitCursor;
                bool installed = Updater.Run(appDir, seedIni);
                Cursor.Current = Cursors.Default;
                if (installed) return RestartUpdated;
            }

            string workRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Unplugged");
            // 2.56.0: and refreshes ClipGuard's copy of Unplugged with the renderer next to
            // ClipGuard.exe, as ClipGuard does on its start (an update takes effect at once).
            try { Program.RefreshCopy(appDir, workRoot); }
            catch (Exception ex) { GitHub.Note("update", "Player: refreshing ClipGuard's copy of Unplugged failed: " + ex.Message); }
            string srcFilters = Path.Combine(workRoot, "filters");
            if (!File.Exists(Path.Combine(srcFilters, "unplugged_vr.ax")))
                throw new Exception("Start ClipGuard once first: the player takes its decoders and the ClipGuard renderer from ClipGuard's copy of Unplugged.");

            // The decoders, the renderer and its clipguard.ini / LUTs, as ClipGuard has them now.
            CopyNewer(srcFilters, Path.Combine(exeDir, "filters"));
            // 2.63.3: MPC-BE's MPC Audio Renderer (bitstream) ships next to the player; the
            // engine loads it from its filters folder.
            string mar = Path.Combine(exeDir, "MpcAudioRenderer.ax");
            if (File.Exists(mar)) CopyIfNewer(mar, Path.Combine(exeDir, "filters", "MpcAudioRenderer.ax"));
            // Runtime DLLs next to Unplugged.exe (not its engine: ours is here).
            foreach (string f in Directory.GetFiles(workRoot, "*.dll"))
            {
                string name = Path.GetFileName(f);
                if (string.Equals(name, "s3engine.dll", StringComparison.OrdinalIgnoreCase)) continue;
                CopyIfNewer(f, Path.Combine(exeDir, name));
            }

            // 2.54.0: ClipGuard itself runs in the player (F7 menu, its keys, the info banner),
            // on the same settings file as the video processor, so changes carry both ways.
            ControlForm.PlayerMode = true;
            ControlForm cg = new ControlForm(Path.Combine(srcFilters, "clipguard.ini"), seedIni);
            cg.OpenPlayer(appDir, workRoot);
            Application.Run(cg);
            return 0;
        }

#endif

        static void CopyNewer(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) CopyIfNewer(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (string d in Directory.GetDirectories(src)) CopyNewer(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        static void CopyIfNewer(string src, string dst)
        {
            FileInfo a = new FileInfo(src), b = new FileInfo(dst);
            if (b.Exists && b.Length == a.Length && b.LastWriteTimeUtc == a.LastWriteTimeUtc) return;
            File.Copy(src, dst, true);
            File.SetLastWriteTimeUtc(dst, a.LastWriteTimeUtc);
        }
    }
}
#endif
