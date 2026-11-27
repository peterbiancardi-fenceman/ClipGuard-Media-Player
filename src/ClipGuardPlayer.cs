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
        public const string MediaPlayerVersion = "1.1.0";   // the public release's own numbering
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
        // Neither Unplugged nor ClipGuard.exe is used.
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

            ControlForm.PlayerMode = true;
            ControlForm cg = new ControlForm(liveIni, seedIni);
            cg.OpenPlayer(exeDir, dataDir);
            Application.Run(cg);
            return 0;
        }

        // The first start: the settings files and the Calibration folder.
        static void Migrate(string seedIni, string liveIni)
        {
            try
            {
                if (!File.Exists(seedIni)) File.WriteAllText(seedIni, "[ClipGuard]\r\n[MediaPlayer]\r\n");
                if (!File.Exists(liveIni)) File.Copy(seedIni, liveIni);
                Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Calibration"));
            }
            catch (Exception) { }
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
