// ClipGuard Meter 1.16.
//
// Measures how a projector's laser dimming behaves, with an i1Display Pro (or
// any meter ArgyllCMS's spotread can drive) pointed at the centre of the
// screen. It draws its own test patterns full screen and reads the meter
// between them; results are saved as meter_<time>.csv on this PC.
//
// Built from this file plus ClipGuard.cs (/main:ClipGuard.MeterProgram) so it
// shares the ini code. C# 5.
//
// Keys:  C = check the meter (Argyll found, meters on USB, one reading)
//        A = aim the meter (target + live reading; Enter when done)
//        T = DBLE test (2 min)   S = all DBLE tests (10 min)   R = transitions (12 min)   G = clip test, colour and size (12 min)   (after aiming)
//        Esc = stop / quit   M = move to the next monitor
//
// Steps of a run:
//   0  checks (white, grey, black, full white) and settle time from black
//   1  transitions: a dark scene held until the laser has settled down, then
//      a highlight level appears in the centre patch; the meter reads as fast
//      as it can for 4 s after the switch (start and end time of every
//      reading are kept), so clipping while the laser catches up shows as
//      near-white levels reading the same. Full run: 4 dark scenes x 6 levels
//      x 3 repeats. Test run: 1 dark scene x 2 levels x 2 repeats.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ClipGuard
{
    static class MeterProgram
    {
        [STAThread]
        static int Main()
        {
            Application.EnableVisualStyles();
            CalApp.Parse(Environment.GetCommandLineArgs());   // 2.66.0: the player's projector calibration
            try { Application.Run(new MeterForm()); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ClipGuard Meter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
    }

    // What is on screen. Levels are 8-bit greys (0-255).
    class MScene
    {
        public string Id = "";
        public int Bg;            // background level
        public int Patch = -1;    // centre patch level, -1 = same as background
        public int BoxLevel;      // bright boxes level
        public double BoxArea;    // bright boxes, share of the screen (0 = none)
        public string BoxPos = "sides"; // sides, corner, left, top
        public bool Ramp;         // background is a left-to-right ramp 0-255
        // 1.2: lights for the DBLE tests. Lights > 0: that many squares sharing
        // BoxArea, at LightPos, in grey BoxLevel or the colour R, G, B.
        public int Lights;
        public string LightPos = "right";   // right, near, left, top, bottom, corner
        public int R = -1, G = -1, B = -1;
        public double PatchArea;  // 1.3: centre patch area, share of the screen (0 = the usual 10%-wide square)
        public int FR = -1, FG, FB;  // 1.4: auto-cal: the whole screen in this colour
        public string Note;          // 1.4: auto-cal: an instruction at the bottom
        public double Window;        // 1.6: auto-cal: the colour only in a centred window of this share of the screen, black around it
        public Bitmap Frame;         // 1.9: frame test: a picture, its point (FrameX, FrameY) at the screen centre, black around it
        public float FrameX, FrameY;
        public float FrameGain = 1f; // 1.9: the picture's levels times this
        public Bitmap Canvas;        // 1.10: histogram test: the whole screen, drawn 1:1 (the patch on top)
        public bool NoText;          // 1.16: colour test: no progress text on screen
        public MScene Copy() { return (MScene)MemberwiseClone(); }
    }

    // One meter reading.
    class MReading
    {
        public bool Ok;
        public double X, Y, Z;
        public double T0, T1;     // seconds since the run started: request sent, result received
        public string Error = "";
    }

    // ArgyllCMS spotread, kept running (ARGYLL_NOT_INTERACTIVE: keys come from
    // stdin, as DisplayCAL drives it), so a reading costs only the meter's own
    // time. If that does not answer, each reading runs spotread -O instead.
    class Spotread : IDisposable
    {
        public string Exe = "";
        public string Args = "-e -N";
        public string Key = " ";
        public bool OneShot;            // fell back to one spotread per reading
        public string Banner = "";      // first lines spotread printed (version, instrument)
        Process proc;
        readonly object gate = new object();
        readonly StringBuilder outBuf = new StringBuilder();
        readonly Stopwatch clock;
        static readonly Regex XyzRx = new Regex(@"XYZ:\s*([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)");

        public Spotread(Stopwatch clock) { this.clock = clock; }

        // 2.88.4: Esc / stop ends a reading that is waiting on the meter at once.
        public static volatile bool Abort;

        // 2.88.4: a spotread left over from a run that hung still has the meter open,
        // so no new one can read; end any before starting.
        public static int KillStale()
        {
            int n = 0;
            try
            {
                foreach (Process p in Process.GetProcessesByName("spotread"))
                {
                    try { p.Kill(); p.WaitForExit(3000); n++; } catch (Exception) { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception) { }
            if (n > 0) Thread.Sleep(1500);   // the meter is free again after a moment
            return n;
        }

        public static string Find(string iniPath)
        {
            // 2.66.0: the player's own ArgyllCMS copy first.
            if (CalApp.ArgyllBin.Length > 0)
            {
                string own = Path.Combine(CalApp.ArgyllBin, "spotread.exe");
                if (File.Exists(own)) return own;
            }
            string p = Native.ReadIni(iniPath, "Meter", "SpotreadPath", "").Trim().Trim('"');
            if (p.Length > 0 && File.Exists(p)) return p;
            List<string> dirs = new List<string>();
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(';')) if (d.Trim().Length > 0) dirs.Add(d.Trim().Trim('"'));
            List<string> roots = new List<string>();
            roots.Add(@"C:\");
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            roots.Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "");
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"DisplayCAL\dl"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"DisplayCAL\dl"));
            foreach (string r in roots)
            {
                try
                {
                    if (r.Length == 0 || !Directory.Exists(r)) continue;
                    foreach (string d in Directory.GetDirectories(r, "Argyll*")) dirs.Add(Path.Combine(d, "bin"));
                }
                catch (Exception) { }
            }
            foreach (string d in dirs)
            {
                try { string f = Path.Combine(d, "spotread.exe"); if (File.Exists(f)) return f; }
                catch (Exception) { }
            }
            return "";
        }

        public bool Start(out string error)
        {
            error = "";
            KillStale();   // 2.88.4: a hung spotread from an earlier run holds the meter
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Exe, Args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.EnvironmentVariables["ARGYLL_NOT_INTERACTIVE"] = "1";
                proc = Process.Start(psi);
                StartReader(proc.StandardOutput);
                StartReader(proc.StandardError);
                // Wait for the prompt that says it is ready to read.
                DateTime until = DateTime.Now.AddSeconds(20);
                while (DateTime.Now < until)
                {
                    string all;
                    lock (gate) all = outBuf.ToString();
                    if (all.IndexOf("to read", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf("hit", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Banner = Head(all, 6);
                        return true;
                    }
                    if (proc.HasExited)
                    {
                        error = "spotread stopped: " + Head(all, 8);
                        return false;
                    }
                    Thread.Sleep(50);
                }
                string a2; lock (gate) a2 = outBuf.ToString();
                error = "spotread did not get ready: " + Head(a2, 8);
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        static string Head(string s, int lines)
        {
            string[] l = s.Replace("\r", "").Split('\n');
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < l.Length && i < lines; i++) { if (l[i].Trim().Length > 0) b.Append(l[i].Trim()).Append(" | "); }
            return b.ToString();
        }

        void StartReader(StreamReader r)
        {
            Thread t = new Thread(() =>
            {
                char[] buf = new char[256];
                try
                {
                    int n;
                    while ((n = r.Read(buf, 0, buf.Length)) > 0)
                        lock (gate) outBuf.Append(buf, 0, n);
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        public MReading Read(int timeoutMs)
        {
            if (OneShot) return ReadOneShot(timeoutMs);
            MReading m = new MReading();
            int from;
            lock (gate) from = outBuf.Length;
            m.T0 = clock.Elapsed.TotalSeconds;
            try
            {
                proc.StandardInput.Write(Key);
                proc.StandardInput.Flush();
            }
            catch (Exception ex) { m.Error = "write: " + ex.Message; return m; }
            DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until)
            {
                string s;
                lock (gate) s = outBuf.ToString(from, outBuf.Length - from);
                Match x = XyzRx.Match(s);
                if (x.Success)
                {
                    m.T1 = clock.Elapsed.TotalSeconds;
                    m.Ok = ParseXyz(x, m);
                    return m;
                }
                if (proc.HasExited) { m.Error = "spotread stopped: " + Head(s, 4); return m; }
                if (Abort) { m.Error = "stopped"; return m; }
                Thread.Sleep(5);
            }
            m.Error = "no answer";
            return m;
        }

        MReading ReadOneShot(int timeoutMs)
        {
            MReading m = new MReading();
            m.T0 = clock.Elapsed.TotalSeconds;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Exe, Args + " -O");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    // 2.88.4: both outputs read on their own threads and a time limit on
                    // the process itself (a hung spotread used to block here for ever)
                    string so = "", se = "";
                    Thread t = new Thread(() => { try { so = p.StandardOutput.ReadToEnd(); } catch (Exception) { } });
                    Thread te = new Thread(() => { try { se = p.StandardError.ReadToEnd(); } catch (Exception) { } });
                    t.IsBackground = true; t.Start();
                    te.IsBackground = true; te.Start();
                    DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
                    while (!p.HasExited && DateTime.Now < until && !Abort) Thread.Sleep(20);
                    if (!p.HasExited) { try { p.Kill(); } catch (Exception) { } m.Error = Abort ? "stopped" : "no answer"; return m; }
                    t.Join(2000); te.Join(2000);
                    m.T1 = clock.Elapsed.TotalSeconds;
                    Match x = XyzRx.Match(so + "\n" + se);
                    if (x.Success) m.Ok = ParseXyz(x, m);
                    else m.Error = "no reading: " + Head(so + "\n" + se, 4);
                }
            }
            catch (Exception ex) { m.Error = ex.Message; }
            return m;
        }

        static bool ParseXyz(Match x, MReading m)
        {
            return double.TryParse(x.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out m.X)
                && double.TryParse(x.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out m.Y)
                && double.TryParse(x.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out m.Z);
        }

        public void Dispose()
        {
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    try { proc.StandardInput.Write("q"); proc.StandardInput.Flush(); } catch (Exception) { }
                    if (!proc.WaitForExit(1500)) proc.Kill();
                }
            }
            catch (Exception) { }
            proc = null;
        }
    }

    partial class MeterForm : Form
    {
        readonly string appDir;
        readonly string seedIni;
        readonly Stopwatch clock = new Stopwatch();
        volatile MScene scene;
        volatile string status = "";
        volatile bool idle = true;
        volatile bool cancelFlag;
        // 2.88.4: stopping a run also stops a meter reading that is waiting
        bool cancel { get { return cancelFlag; } set { cancelFlag = value; Spotread.Abort = value; } }
        string idleText = "";
        Thread worker;
        int screenIndex;
        volatile bool aiming;      // the Aim screen is up
        // 1.9: frame test (F): the log pictures to choose from, newest first.
        volatile bool framePick;
        List<string> frameFiles = new List<string>();
        int frameIdx;
        Bitmap frameBmp;
        float frameX, frameY;
        int framePeak;
        volatile bool aimed;       // aimed at least once; T and S need it
        volatile string aimText = "";
        volatile bool aimSmall;    // 1.7: Tab on the Aim screen: the small box (0.03%, as in the clip test)
        volatile int aimGood;      // good readings on the Aim screen
        volatile string aimEnd = "";  // why the Aim screen ended, if not by Enter or Esc

        public MeterForm()
        {
            appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            seedIni = Path.Combine(appDir, "clipguard.ini");
            Text = "ClipGuard Meter";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor.Hide();
            Screen[] all = Screen.AllScreens;
            Screen cur = Screen.FromPoint(Cursor.Position);
            for (int i = 0; i < all.Length; i++) if (all[i].DeviceName == cur.DeviceName) screenIndex = i;
            if (CalApp.On && CalApp.Screen.Length > 0)
                for (int i = 0; i < all.Length; i++) if (string.Equals(all[i].DeviceName, CalApp.Screen, StringComparison.OrdinalIgnoreCase)) screenIndex = i;
            PlaceOnScreen();
            scene = new MScene { Id = "idle", Bg = 16, Patch = -1 };
            if (CalApp.On)
            {
                // 2.66.0: straight to the calibration's setup (the player's Calibrate a projector).
                Text = "ClipGuard Calibration";
                calSetup = true;
                calRow = 1;
                SetIdle(CalSetupText());
            }
            else SetIdle(Intro());
        }

        string Intro()
        {
            return "ClipGuard Meter 1.16  -  laser dimming measurement\n\n"
                + "1. Close Unplugged (ClipGuard must not touch these patterns).\n"
                + "2. Put the meter on a tripod, pointing at the centre of the screen.\n"
                + "   Dark room. Leave the projector's settings as you watch films.\n"
                + "3. Press C to check the meter (Argyll found, meter seen, one reading).\n"
                + "   Press A to aim: a target and a live reading show where to point the meter;\n"
                + "   Enter when it is steady and at its highest.\n"
                + "4. T = DBLE test (about 2 minutes), S = all DBLE tests (about 10 minutes),\n"
                + "   R = transitions (about 12 minutes), G = clip test (colour and size, about 12 minutes),\n"
                + "   F = frame test: a picture from a ClipGuard log (F11), its brightest spot under the meter,\n"
                + "   at 100% down to 50% brightness (about 2 minutes).\n"
                + "   H = histogram test: what the dimming goes by, and how fast it follows a flare;\n"
                + "   no text on screen while it runs (about 18 minutes).\n"
                + "   B = threshold map: how much bright content keeps the laser up, on backgrounds 8 to 64 (about 8 minutes).\n"
                + "   D = fall test: how fast the laser dims after a bright scene cuts to a dark one (about 2 minutes).\n"
                + "   K = colour test: colour shifts at each dimming level, each colour's clip point, and the fall test\n"
                + "   (about 8 minutes). Run it once each with DBLE Off, Low and High. Uses titan.ccss if it is there.\n"
                + "   Run T, S and G once with DBLE Off and once on High.\n"
                + "   L = auto-cal: builds a 3D LUT for the Titan or the Valerion (needs titan.ccss /\n"
                + "   valerion.ccss next to ClipGuardMeter.exe; laser dimming off; about 15 minutes).\n\n"
                + "Esc stops (what was measured is still saved).  M = next monitor.\n\n"
                + "Results are saved as meter_<date>.csv when a run finishes.";
        }

        void PlaceOnScreen()
        {
            Screen[] all = Screen.AllScreens;
            Screen s = all[Math.Max(0, Math.Min(screenIndex, all.Length - 1))];
            Bounds = s.Bounds;
        }

        void SetIdle(string text)
        {
            idleText = text;
            idle = true;
            scene = new MScene { Id = "idle", Bg = 16, Patch = -1 };
            if (IsHandleCreated) BeginInvoke((Action)Invalidate);
        }

        // Keys are taken here, before Windows treats Enter and Esc as dialog keys.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Enter || k == Keys.Return || k == Keys.Escape || k == Keys.A || k == Keys.C
                || k == Keys.T || k == Keys.S || k == Keys.R || k == Keys.G || k == Keys.M || k == Keys.F || k == Keys.H || k == Keys.B || k == Keys.D || k == Keys.K
                || k == Keys.L || k == Keys.N || k == Keys.Tab || k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right)
            {
                HandleKey(k);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void HandleKey(Keys key)
        {
            KeyEventArgs e = new KeyEventArgs(key);
            if (!aiming && CalKey(key)) { Invalidate(); return; }
            if (aiming && e.KeyCode == Keys.Tab)
            {
                aimSmall = !aimSmall;
                scene = AimScene();
                Invalidate();
                return;
            }
            if (aiming)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    if (aimGood > 0) { aimed = true; aiming = false; }
                    else aimText = "No good reading yet - Enter works once the meter reads. " + aimText;
                    Invalidate();
                }
                else if (e.KeyCode == Keys.Escape) aiming = false;
                return;
            }
            if (framePick)
            {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                {
                    if (frameFiles.Count > 1)
                    {
                        frameIdx = (frameIdx + (e.KeyCode == Keys.Right ? 1 : frameFiles.Count - 1)) % frameFiles.Count;
                        LoadFrame();
                    }
                    Invalidate();
                }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    if (frameBmp != null)
                    {
                        framePick = false;
                        idle = false;
                        cancel = false;
                        worker = new Thread(() => Run(4));
                        worker.IsBackground = true;
                        worker.Start();
                    }
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    framePick = false;
                    SetIdle(Intro());
                }
                return;
            }
            if (idle && e.KeyCode == Keys.C)
            {
                idle = false;
                cancel = false;
                worker = new Thread(Check);
                worker.IsBackground = true;
                worker.Start();
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                if (!idle) { cancel = true; status = "Stopping..."; Invalidate(); }
                else Close();
            }
            // 2.97.1: opened from the player, only calibration works (L, N, C, Esc): the DBLE
            // and meter tests on the other keys stay off
            else if (CalApp.On) { }
            else if (idle && e.KeyCode == Keys.A)
            {
                idle = false;
                cancel = false;
                aimGood = 0;
                aimEnd = "";
                aiming = true;
                worker = new Thread(Aim);
                worker.IsBackground = true;
                worker.Start();
            }
            else if (idle && !aimed && (e.KeyCode == Keys.T || e.KeyCode == Keys.S || e.KeyCode == Keys.R || e.KeyCode == Keys.G || e.KeyCode == Keys.F || e.KeyCode == Keys.H || e.KeyCode == Keys.B || e.KeyCode == Keys.D || e.KeyCode == Keys.K))
            {
                SetIdle(">>> AIM FIRST: press A, then Enter when the reading is steady. <<<\n\n" + Intro());
            }
            else if (idle && e.KeyCode == Keys.M)
            {
                screenIndex = (screenIndex + 1) % Screen.AllScreens.Length;
                PlaceOnScreen();
                Invalidate();
            }
            else if (idle && e.KeyCode == Keys.F)
            {
                // 1.9: frame test: choose a log picture (newest first), then Enter.
                frameFiles.Clear();
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "logs");
                    if (Directory.Exists(dir))
                    {
                        string[] f = Directory.GetFiles(dir, "*.png");
                        Array.Sort(f, (x, y) => File.GetLastWriteTimeUtc(y).CompareTo(File.GetLastWriteTimeUtc(x)));
                        frameFiles.AddRange(f);
                    }
                }
                catch (Exception) { }
                if (frameFiles.Count == 0)
                {
                    SetIdle(">>> NO LOG PICTURES: press F11 while the scene plays in ClipGuard first. <<<\n\n" + Intro());
                    return;
                }
                frameIdx = 0;
                LoadFrame();
                idle = false;
                framePick = true;
                Invalidate();
            }
            else if (idle && (e.KeyCode == Keys.T || e.KeyCode == Keys.S || e.KeyCode == Keys.R || e.KeyCode == Keys.G || e.KeyCode == Keys.H || e.KeyCode == Keys.B || e.KeyCode == Keys.D || e.KeyCode == Keys.K))
            {
                int mode = e.KeyCode == Keys.T ? 0 : (e.KeyCode == Keys.S ? 1 : (e.KeyCode == Keys.R ? 2 : (e.KeyCode == Keys.G ? 3 : (e.KeyCode == Keys.H ? 5 : (e.KeyCode == Keys.D ? 7 : (e.KeyCode == Keys.K ? 8 : 6))))));
                idle = false;
                cancel = false;
                worker = new Thread(() => Run(mode));
                worker.IsBackground = true;
                worker.Start();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // While a run is going, closing only stops it (what was measured is
            // still saved); the window closes from the finished screen.
            if (worker != null && worker.IsAlive)
            {
                cancel = true;
                status = "Stopping...";
                e.Cancel = true;
                return;
            }
            Cursor.Show();
            base.OnFormClosing(e);
        }

        // ---- drawing ----------------------------------------------------------

        static Color G(int v) { v = Math.Max(0, Math.Min(255, v)); return Color.FromArgb(v, v, v); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width, H = ClientSize.Height;
            MScene sc = scene;
            if (sc.FR >= 0)
            {
                // Auto-cal: the whole screen in one colour; the text is close to the
                // patch colour so it adds almost nothing to the reading.
                int lum = (sc.FR * 2 + sc.FG * 5 + sc.FB) / 8;
                if (sc.Window > 0)
                {
                    float side = (float)Math.Sqrt(sc.Window * W * H);
                    using (SolidBrush bgb = new SolidBrush(G(sc.Bg))) g.FillRectangle(bgb, 0, 0, W, H);   // 1.8: the background level (auto-cal: 0)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(sc.FR, sc.FG, sc.FB))) g.FillRectangle(b, (W - side) / 2f, (H - side) / 2f, side, side);
                    lum = 0;   // text on the black around the window
                    // 1.16: the colour test's control lights (they set the dimming), and no text at all.
                    if (sc.BoxArea > 0 && sc.Lights > 0)
                        using (SolidBrush lb = new SolidBrush(G(sc.BoxLevel)))
                            foreach (RectangleF r in LightRects(sc, W, H)) g.FillRectangle(lb, r);
                    if (sc.NoText) return;
                }
                else
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(sc.FR, sc.FG, sc.FB))) g.FillRectangle(b, 0, 0, W, H);
                if (sc.Note != null)
                    using (Font f = new Font("Segoe UI", Math.Max(12, H / 55f)))
                    using (SolidBrush b = new SolidBrush(lum > 128 ? Color.FromArgb(70, 70, 70) : Color.FromArgb(200, 200, 200)))
                        g.DrawString(sc.Note, f, b, W * 0.05f, H * 0.92f);
                else
                    using (Font f = new Font("Segoe UI", Math.Max(9, H / 90f)))
                    using (SolidBrush b = new SolidBrush(lum > 128 ? Color.FromArgb(Math.Max(0, sc.FR - 20), Math.Max(0, sc.FG - 20), Math.Max(0, sc.FB - 20)) : Color.FromArgb(Math.Min(255, sc.FR + 20), Math.Min(255, sc.FG + 20), Math.Min(255, sc.FB + 20))))
                        g.DrawString(status, f, b, W * 0.01f, H * 0.965f);
                return;
            }
            if (sc.Frame != null)
            {
                // 1.9: frame test: the picture at its own size on a 4K screen, its
                // brightest spot at the centre (where the meter reads), levels x FrameGain.
                g.FillRectangle(Brushes.Black, 0, 0, W, H);
                float s = Math.Min((float)W / 3840f, (float)H / 2160f);
                RectangleF dst = new RectangleF(W / 2f - sc.FrameX * s, H / 2f - sc.FrameY * s, sc.Frame.Width * s, sc.Frame.Height * s);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    float k = sc.FrameGain;
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new float[][] {
                        new float[] { k, 0, 0, 0, 0 }, new float[] { 0, k, 0, 0, 0 }, new float[] { 0, 0, k, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } }));
                    g.DrawImage(sc.Frame, Rectangle.Round(dst), 0, 0, sc.Frame.Width, sc.Frame.Height, GraphicsUnit.Pixel, ia);
                }
                if (framePick)
                {
                    // The meter's aim point (yellow, white outline) and the choice.
                    float cx = W / 2f, cy = H / 2f, r = H * 0.03f;
                    using (Pen pw = new Pen(Color.White, 6)) g.DrawEllipse(pw, cx - r, cy - r, 2 * r, 2 * r);
                    using (Pen py = new Pen(Color.FromArgb(255, 212, 0), 3)) g.DrawEllipse(py, cx - r, cy - r, 2 * r, 2 * r);
                    using (Font f = new Font("Segoe UI", Math.Max(12, H / 50f)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 200, 200)))
                        g.DrawString("F = frame test.  Picture " + (frameIdx + 1) + " of " + frameFiles.Count + ": " + Path.GetFileName(frameFiles[frameIdx])
                            + "  (brightest " + framePeak + ", placed in the circle)\nLeft / Right: other log pictures.   Enter: start (about 2 minutes).   Esc: back.\n"
                            + "Run it once with laser dimming Off and once on High.", f, b, W * 0.03f, H * 0.04f);
                    return;
                }
                using (Font f = new Font("Segoe UI", Math.Max(9, H / 90f)))
                using (SolidBrush b = new SolidBrush(G(14)))
                    g.DrawString(status, f, b, W * 0.01f, H * 0.965f);
                return;
            }
            if (sc.Ramp)
            {
                for (int x = 0; x < W; x += 4)
                    using (SolidBrush b = new SolidBrush(G((int)Math.Round(255.0 * x / Math.Max(1, W - 1)))))
                        g.FillRectangle(b, x, 0, 4, H);
            }
            else if (sc.Canvas != null)
            {
                // 1.10: histogram test: the prepared screen, pixel for pixel.
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(sc.Canvas, new Rectangle(0, 0, sc.Canvas.Width, sc.Canvas.Height));
            }
            else
                using (SolidBrush b = new SolidBrush(G(sc.Bg))) g.FillRectangle(b, 0, 0, W, H);

            if (sc.BoxArea > 0 && sc.Lights > 0)
                using (SolidBrush b = new SolidBrush(sc.R >= 0 ? Color.FromArgb(sc.R, sc.G, sc.B) : G(sc.BoxLevel)))
                    foreach (RectangleF r in LightRects(sc, W, H)) g.FillRectangle(b, r);
            else if (sc.BoxArea > 0)
                using (SolidBrush b = new SolidBrush(G(sc.BoxLevel)))
                    foreach (RectangleF r in Boxes(sc, W, H)) g.FillRectangle(b, r);

            if (sc.Patch >= 0)
                using (SolidBrush b = new SolidBrush(G(sc.Patch)))
                {
                    RectangleF pr = PatchRect(W, H);
                    if (sc.PatchArea > 0)
                    {
                        float side = (float)Math.Max(2.0, Math.Sqrt(sc.PatchArea * W * H));
                        pr = new RectangleF((W - side) / 2f, (H - side) / 2f, side, side);
                    }
                    g.FillRectangle(b, pr);
                }

            if (aiming)
            {
                DrawTarget(g, W, H);
                return;
            }
            if (idle)
            {
                DrawCorners(g, W, H, 3);
                using (Font f = new Font("Segoe UI", Math.Max(12, H / 50f)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    g.DrawString(idleText, f, b, W * 0.08f, H * 0.12f);
            }
            else if (sc.Canvas == null)   // 1.11: no text at all on the histogram test's screens
            {
                // Progress in near-black, small, bottom left: almost no light, so
                // it does not change what the projector reacts to.
                using (Font f = new Font("Segoe UI", Math.Max(9, H / 90f)))
                using (SolidBrush b = new SolidBrush(G(Math.Max(sc.Bg, 0) + 14)))
                    g.DrawString(status, f, b, W * 0.01f, H * 0.965f);
            }
        }

        // Corner marks just outside the measured square (yellow, white outline).
        static void DrawCorners(Graphics g, int W, int H, float width)
        {
            RectangleF r = PatchRect(W, H);
            float gap = W * 0.006f, len = r.Width * 0.25f;
            float x0 = r.Left - gap, y0 = r.Top - gap, x1 = r.Right + gap, y1 = r.Bottom + gap;
            PointF[][] marks = {
                new[] { new PointF(x0, y0 + len), new PointF(x0, y0), new PointF(x0 + len, y0) },
                new[] { new PointF(x1 - len, y0), new PointF(x1, y0), new PointF(x1, y0 + len) },
                new[] { new PointF(x0, y1 - len), new PointF(x0, y1), new PointF(x0 + len, y1) },
                new[] { new PointF(x1 - len, y1), new PointF(x1, y1), new PointF(x1, y1 - len) } };
            using (Pen outline = new Pen(Color.White, width * 3))
            using (Pen yellow = new Pen(Color.FromArgb(255, 220, 0), width))
                foreach (PointF[] m in marks) { g.DrawLines(outline, m); g.DrawLines(yellow, m); }
        }

        // The Aim screen: white square, crosshair arms outside it, corner marks,
        // and the live reading.
        void DrawTarget(Graphics g, int W, int H)
        {
            RectangleF r = PatchRect(W, H);
            if (aimSmall)
            {
                float side = (float)Math.Max(2.0, Math.Sqrt(0.0003 * W * H));
                r = new RectangleF((W - side) / 2f, (H - side) / 2f, side, side);
            }
            float lw = Math.Max(3f, W / 640f);
            float cx = W / 2f, cy = H / 2f, gap = aimSmall ? W * 0.012f : W * 0.02f, arm = aimSmall ? W * 0.25f : W * 0.12f;
            using (Pen outline = new Pen(Color.White, lw * 3))
            using (Pen yellow = new Pen(Color.FromArgb(255, 220, 0), lw))
            {
                PointF[][] arms = {
                    new[] { new PointF(r.Left - gap - arm, cy), new PointF(r.Left - gap, cy) },
                    new[] { new PointF(r.Right + gap, cy), new PointF(r.Right + gap + arm, cy) },
                    new[] { new PointF(cx, r.Top - gap - arm), new PointF(cx, r.Top - gap) },
                    new[] { new PointF(cx, r.Bottom + gap), new PointF(cx, r.Bottom + gap + arm) } };
                foreach (PointF[] a in arms) { g.DrawLines(outline, a); g.DrawLines(yellow, a); }
            }
            DrawCorners(g, W, H, lw);
            using (Font big = new Font("Segoe UI", Math.Max(16, H / 30f), FontStyle.Bold))
            using (Font f = new Font("Segoe UI", Math.Max(12, H / 55f)))
            using (SolidBrush white = new SolidBrush(Color.White))
            using (SolidBrush yellowB = new SolidBrush(Color.FromArgb(255, 220, 0)))
            {
                g.DrawString(aimSmall ? "AIM (small box): diffuser at the screen, facing the lens; the small box on the diffuser"
                                      : "AIM: diffuser at the screen, facing the lens, on the white square   (for the DBLE tests)", f, white, W * 0.05f, H * 0.06f);
                g.DrawString(aimText, big, yellowB, W * 0.05f, H * 0.11f);
                g.DrawString("Move the meter until the reading is steady and at its highest.\nTab = " + (aimSmall ? "large square" : "small box") + "   Enter = done   Esc = cancel", f, white, W * 0.05f, H * 0.80f);
            }
        }

        // 1.7: the meter setup is chosen by the test, not by clipguard.ini: the DBLE
        // tests (A, T, S, R, G) read through the diffuser at the screen, facing the
        // lens (spotread -a); auto-cal and C read off the screen. A "-a" left in
        // SpotreadArgs is ignored.
        string MeterArgs(bool diffuser)
        {
            string a = Native.ReadIni(seedIni, "Meter", "SpotreadArgs", "-e -N").Trim();
            List<string> keep = new List<string>();
            foreach (string t in a.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (t != "-a" && !(diffuser && t == "-e")) keep.Add(t);
            if (diffuser) keep.Add("-a");
            return string.Join(" ", keep.ToArray());
        }

        // Check: Argyll found, its version, the meters it sees, one reading.
        void Check()
        {
            List<string> r = new List<string>();
            try
            {
                SetBusyText("Checking the meter...");
                string exe = Spotread.Find(seedIni);
                if (exe.Length == 0)
                {
                    r.Add("spotread.exe: NOT FOUND. Install ArgyllCMS, or put its path in clipguard.ini:");
                    r.Add("   [Meter]  SpotreadPath=C:\\Argyll_V3.x\\bin\\spotread.exe");
                    return;
                }
                r.Add("spotread.exe: " + exe);
                string usage = "";
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe, "-?");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                    using (Process p = Process.Start(psi))
                    {
                        string so = "";
                        Thread t = new Thread(() => { try { so = p.StandardOutput.ReadToEnd(); } catch (Exception) { } });
                        t.IsBackground = true; t.Start();
                        string se = p.StandardError.ReadToEnd();
                        p.WaitForExit(15000); t.Join(2000);
                        usage = so + "\n" + se;
                    }
                }
                catch (Exception ex) { r.Add("Could not run spotread: " + ex.Message); return; }
                string ver = "", meters = "";
                foreach (string line in usage.Replace("\r", "").Split('\n'))
                {
                    string l = line.Trim();
                    if (ver.Length == 0 && l.IndexOf("Version", StringComparison.OrdinalIgnoreCase) >= 0) ver = l;
                    if (Regex.IsMatch(l, @"^\d+\s*=\s*'") || l.IndexOf("No ports found", StringComparison.OrdinalIgnoreCase) >= 0)
                        meters += "   " + l + "\n";
                }
                r.Add("Argyll: " + (ver.Length > 0 ? ver : "(version not shown)"));
                r.Add("Meters Argyll sees on USB:\n" + (meters.Length > 0 ? meters.TrimEnd('\n') : "   (none listed)"));
                if (meters.IndexOf("No ports found", StringComparison.OrdinalIgnoreCase) >= 0 || meters.Length == 0)
                {
                    r.Add("No meter found: plug it in, close X-Rite / Calman / DisplayCAL, and press C again.");
                    return;
                }
                Spotread sp = new Spotread(clock);
                sp.Exe = exe;
                sp.Args = MeterArgs(false);
                string key = Native.ReadIni(seedIni, "Meter", "ReadKey", "space").Trim();
                sp.Key = key.Equals("space", StringComparison.OrdinalIgnoreCase) || key.Length == 0 ? " " : key.Substring(0, 1);
                clock.Restart();
                try
                {
                    string err;
                    bool kept = sp.Start(out err);
                    if (!kept) { r.Add("Kept-running mode: failed (" + err + "); trying one reading per run."); sp.Dispose(); sp.OneShot = true; }
                    else r.Add("Kept-running mode: OK.");
                    MReading m = sp.Read(30000);
                    if (m.Ok) r.Add(string.Format(CultureInfo.InvariantCulture, "Test reading: OK, {0:0.###} cd/m2 (it does not matter where the meter points for this).", m.Y));
                    else r.Add("Test reading: FAILED (" + m.Error + ").");
                }
                finally { sp.Dispose(); }
            }
            catch (Exception ex) { r.Add("Error: " + ex.Message); }
            finally
            {
                SetIdle("METER CHECK\n\n" + string.Join("\n", r.ToArray()) + "\n\nC = check again   A = aim   Esc = quit");
            }
        }

        void SetBusyText(string t)
        {
            idleText = t;
            scene = new MScene { Id = "idle", Bg = 16, Patch = -1 };
            try { BeginInvoke((Action)(() => { idle = true; Invalidate(); idle = false; })); } catch (Exception) { }
        }

        // Aim: live readings on a white square until Enter or Esc.
        MScene AimScene() { return new MScene { Id = "aim", Bg = 0, Patch = 255, PatchArea = aimSmall ? 0.0003 : 0 }; }

        void Aim()
        {
            Spotread sp = new Spotread(clock);
            try
            {
                aimText = "Starting the meter...";
                scene = AimScene();
                try { BeginInvoke((Action)Invalidate); } catch (Exception) { }
                sp.Exe = Spotread.Find(seedIni);
                if (sp.Exe.Length == 0) { aimEnd = "spotread.exe not found"; aiming = false; SetIdle("ArgyllCMS spotread.exe was not found.\n\nInstall ArgyllCMS, or put its path in clipguard.ini:\n[Meter]\nSpotreadPath=C:\\Argyll_V3.x\\bin\\spotread.exe\n\nPress Esc to quit."); return; }
                sp.Args = MeterArgs(true);
                string key = Native.ReadIni(seedIni, "Meter", "ReadKey", "space").Trim();
                sp.Key = key.Equals("space", StringComparison.OrdinalIgnoreCase) || key.Length == 0 ? " " : key.Substring(0, 1);
                clock.Restart();
                string err;
                if (!sp.Start(out err)) { sp.Dispose(); sp.OneShot = true; }
                double best = 0;
                int fails = 0;
                while (aiming && !cancel)
                {
                    MReading m = sp.Read(30000);
                    if (m.Ok)
                    {
                        aimGood++;
                        fails = 0;
                        best = Math.Max(best, m.Y);
                        aimText = string.Format(CultureInfo.InvariantCulture, "{0:0.0} cd/m2      highest so far {1:0.0}", m.Y, best);
                    }
                    else
                    {
                        aimText = "No reading: " + m.Error;
                        if (++fails >= 3) { aimEnd = "the meter gave no reading 3 times (" + m.Error + "). Press C to check it."; break; }
                    }
                    try { BeginInvoke((Action)Invalidate); } catch (Exception) { }
                }
            }
            catch (Exception ex) { aimEnd = "error: " + ex.Message; }
            finally
            {
                sp.Dispose();
                aiming = false;
                if (aimed) SetIdle(">>> Aimed. Press T for the test run or S for the full run. <<<\n\n" + Intro());
                else if (aimEnd.Length > 0) SetIdle(">>> Aiming stopped: " + aimEnd + " <<<\n\n" + Intro());
                else SetIdle(Intro());
            }
        }

        // Centre patch: a square 10% of the screen width (where the meter points).
        public static RectangleF PatchRect(int W, int H)
        {
            float s = W * 0.10f;
            return new RectangleF((W - s) / 2f, (H - s) / 2f, s, s);
        }

        // 1.2: Lights squares sharing BoxArea of the screen, centred on LightPos
        // (never on the centre square the meter reads), spaced 4% of the width.
        public static List<RectangleF> LightRects(MScene sc, int W, int H)
        {
            List<RectangleF> list = new List<RectangleF>();
            double cx = 0.75, cy = 0.5;
            switch (sc.LightPos)
            {
                case "near": cx = 0.62; cy = 0.5; break;
                case "left": cx = 0.25; cy = 0.5; break;
                case "top": cx = 0.5; cy = 0.12; break;
                case "bottom": cx = 0.5; cy = 0.88; break;
                case "corner": cx = 0.10; cy = 0.12; break;
            }
            int n = Math.Max(1, sc.Lights);
            double side = Math.Max(1.0, Math.Sqrt(sc.BoxArea / n * W * H));
            int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (n + cols - 1) / cols;
            double gap = Math.Max(side * 2, W * 0.04);
            for (int i = 0; i < n; i++)
            {
                int c = i % cols, r = i / cols;
                double x = cx * W + (c - (cols - 1) / 2.0) * gap - side / 2;
                double y = cy * H + (r - (rows - 1) / 2.0) * gap - side / 2;
                list.Add(new RectangleF((float)x, (float)y, (float)side, (float)side));
            }
            return list;
        }

        // Bright boxes covering BoxArea of the screen, kept away from the patch.
        public static List<RectangleF> Boxes(MScene sc, int W, int H)
        {
            List<RectangleF> list = new List<RectangleF>();
            double area = sc.BoxArea * W * H;
            if (sc.BoxPos == "sides")
            {
                double a = area / 2;
                double h = Math.Min(H, Math.Sqrt(a));
                double w = Math.Min(W * 0.4, a / h);
                foreach (double cx in new double[] { 0.25, 0.75 })
                    list.Add(new RectangleF((float)(cx * W - w / 2), (float)((H - h) / 2), (float)w, (float)h));
            }
            else
            {
                double s = Math.Sqrt(area);
                float x = 0, y = 0;
                if (sc.BoxPos == "corner") { x = (float)(W * 0.03); y = (float)(H * 0.05); }
                else if (sc.BoxPos == "left") { x = (float)(W * 0.03); y = (float)((H - s) / 2); }
                else if (sc.BoxPos == "top") { x = (float)((W - s) / 2); y = (float)(H * 0.05); }
                list.Add(new RectangleF(x, y, (float)s, (float)s));
            }
            return list;
        }

        void Show(MScene sc)
        {
            scene = sc;
            try { Invoke((Action)(() => { Invalidate(); Update(); })); } catch (Exception) { }
            Thread.Sleep(60);   // the frame reaches the projector
        }

        void Status(string s)
        {
            status = s;
            try { BeginInvoke((Action)Invalidate); } catch (Exception) { }
        }

        // ---- the run ----------------------------------------------------------

        StringBuilder csv;
        double settleSec = 3.0;

        void Row(string step, MScene sc, int rep, double tSwitch, MReading m)
        {
            csv.AppendFormat(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5:0.####},{6},{7},{8},{9:0.###},{10:0.###},{11:0.###},{12},{13:0.#####},{14:0.#####},{15:0.#####},{16}\n",
                step, sc.Id, sc.Bg, sc.Patch, sc.BoxLevel, sc.BoxArea,
                sc.Lights > 0 ? string.Format(CultureInfo.InvariantCulture, "{0} n{1} rgb{2}", sc.LightPos, sc.Lights, sc.R >= 0 ? sc.R + "-" + sc.G + "-" + sc.B : "grey") : sc.BoxPos,
                sc.Ramp ? 1 : 0, rep,
                tSwitch, m.T0, m.T1, m.Ok ? 1 : 0, m.X, m.Y, m.Z, m.Error.Replace(",", ";"));
        }

        // mode 0 = T (DBLE test, short), 1 = S (DBLE tests, full), 2 = R (transitions, 1.1's full run)
        void Run(int mode)
        {
            bool full = mode != 0;
            string runName = mode == 0 ? "test" : (mode == 1 ? "full" : (mode == 2 ? "transitions" : (mode == 3 ? "clip" : (mode == 4 ? "frame" : (mode == 5 ? "histogram" : (mode == 7 ? "fall" : (mode == 8 ? "colour" : "thresholds")))))));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            csv = new StringBuilder();
            List<string> report = new List<string>();
            Spotread sp = new Spotread(clock);
            reader = sp;
            try
            {
                Status("Starting the meter...");
                sp.Exe = Spotread.Find(seedIni);
                if (sp.Exe.Length == 0)
                {
                    SetIdle("ArgyllCMS spotread.exe was not found.\n\nInstall ArgyllCMS, or put its path in clipguard.ini:\n[Meter]\nSpotreadPath=C:\\Argyll_V3.x\\bin\\spotread.exe\n\nPress Esc to quit.");
                    return;
                }
                sp.Args = MeterArgs(true);
                // 1.16: the colour test reads colour through the projector's .ccss when it is there.
                string kccss = Path.Combine(appDir, "titan.ccss");
                bool kUsesCcss = mode == 8 && File.Exists(kccss);
                if (kUsesCcss) sp.Args = (sp.Args + " -X \"" + kccss + "\"").Trim();
                string key = Native.ReadIni(seedIni, "Meter", "ReadKey", "space").Trim();
                sp.Key = key.Equals("space", StringComparison.OrdinalIgnoreCase) || key.Length == 0 ? " " : key.Substring(0, 1);
                clock.Restart();
                string err;
                if (!sp.Start(out err))
                {
                    sp.Dispose();
                    report.Add("Kept spotread running: failed (" + err + "); using one spotread per reading.");
                    sp.OneShot = true;
                }
                // 1.16: if spotread won't read with the .ccss, the colour test reads without it.
                if (kUsesCcss)
                {
                    Show(new MScene { Id = "ccss_check", Bg = 0, Patch = 255 });
                    Thread.Sleep(1500);
                    MReading probe = Reader.Read(30000);
                    if (!probe.Ok)
                    {
                        report.Add("The meter would not read with titan.ccss (" + probe.Error + "); colour read without it.");
                        sp.Dispose();
                        sp = new Spotread(clock);
                        reader = sp;
                        sp.Exe = Spotread.Find(seedIni);
                        sp.Args = MeterArgs(true);
                        sp.Key = key.Equals("space", StringComparison.OrdinalIgnoreCase) || key.Length == 0 ? " " : key.Substring(0, 1);
                        string err2;
                        if (!sp.Start(out err2)) { sp.Dispose(); sp.OneShot = true; }
                        kUsesCcss = false;
                    }
                }

                Screen scr = Screen.FromControl(this);
                csv.AppendFormat(CultureInfo.InvariantCulture, "# ClipGuard Meter 1.16 {0} run {1}\n", runName, stamp);
                csv.AppendFormat(CultureInfo.InvariantCulture, "# screen {0}x{1} {2}; spotread {3} {4}; one-shot {5}\n",
                    ClientSize.Width, ClientSize.Height, scr.DeviceName, sp.Exe, sp.Args, sp.OneShot ? 1 : 0);
                csv.AppendFormat("# spotread said: {0}\n", sp.Banner.Replace("\n", " "));
                if (mode == 8) csv.AppendFormat("# colour test: ccss {0}\n", kUsesCcss ? "titan.ccss" : "none (titan.ccss not found)");
                csv.Append("step,scene,bg,patch,box_level,box_area,box_pos,ramp,rep,t_switch,t_start,t_end,ok,X,Y,Z,error\n");

                // ---- step 0: checks and settle time ----
                Status("Test: white patch");
                MReading white = Settled("0", new MScene { Id = "white_small", Bg = 0, Patch = 255 }, 3.0);
                if (!white.Ok)
                {
                    report.Add("The meter gave no reading (" + white.Error + "). Is it plugged in, and is Argyll's driver installed?");
                    throw new OperationCanceledException();
                }
                MReading mid = Settled("0", new MScene { Id = "mid_small", Bg = 0, Patch = 128 }, 3.0);
                MReading black = Settled("0", new MScene { Id = "black", Bg = 0, Patch = 0 }, 3.0);
                MReading fullw = Settled("0", new MScene { Id = "white_full", Bg = 255, Patch = 255 }, 3.0);
                MReading wmid = Settled("0", new MScene { Id = "white_on_mid", Bg = 128, Patch = 255 }, 3.0);
                report.Add(string.Format(CultureInfo.InvariantCulture, "Small white patch on black:  {0:0.##} cd/m2", white.Y));
                report.Add(string.Format(CultureInfo.InvariantCulture, "Small mid-grey patch:        {0:0.##} cd/m2", mid.Y));
                report.Add(string.Format(CultureInfo.InvariantCulture, "Black:                       {0:0.###} cd/m2", black.Y));
                report.Add(string.Format(CultureInfo.InvariantCulture, "Full white screen:           {0:0.##} cd/m2", fullw.Y));
                report.Add(string.Format(CultureInfo.InvariantCulture, "White patch on mid-grey:     {0:0.##} cd/m2", wmid.Y));
                bool sane = white.Ok && mid.Ok && white.Y > 1.0 && mid.Y < white.Y * 0.9 && (!black.Ok || black.Y < mid.Y);
                report.Add(sane ? "Readings look right: the meter sees the screen centre."
                                : "Readings look wrong: check that the meter points at the centre square and the room is dark.");

                if (mode == 7)
                {
                    FallTest(report);
                    throw new OperationCanceledException();
                }
                if (mode == 8)
                {
                    ColourTest(report);
                    FallTest(report);
                    throw new OperationCanceledException();
                }

                Status("Test: how long the dimming takes to settle");
                settleSec = SettleTime();
                report.Add(string.Format(CultureInfo.InvariantCulture, "Settles in about {0:0.0} s after a change.", settleSec));
                double wait = Math.Max(1.5, Math.Min(10.0, settleSec * 1.5));

                if (mode == 3)
                {
                    ClipTestColour(report);
                    throw new OperationCanceledException();
                }
                if (mode == 4)
                {
                    FrameTest(report);
                    throw new OperationCanceledException();
                }
                if (mode == 5)
                {
                    HistogramTest(report);
                    throw new OperationCanceledException();
                }
                if (mode == 6)
                {
                    ThresholdMap(report);
                    throw new OperationCanceledException();
                }
                if (mode != 2)
                {
                    Probe(mode == 1, wait, report);
                    throw new OperationCanceledException();   // done: straight to sending
                }

                // ---- step 1: transitions (R) ----
                MScene[] froms = full
                    ? new MScene[] {
                        new MScene { Id = "from_black", Bg = 0, Patch = 0 },
                        new MScene { Id = "from_dark16", Bg = 16, Patch = 16 },
                        new MScene { Id = "from_dark32", Bg = 32, Patch = 32 },
                        new MScene { Id = "from_mid64", Bg = 64, Patch = 64 } }
                    : new MScene[] { new MScene { Id = "from_dark32", Bg = 32, Patch = 32 } };
                int[] levels = full ? new int[] { 180, 210, 225, 235, 245, 255 } : new int[] { 235, 255 };
                int reps = full ? 3 : 2;
                int total = froms.Length * levels.Length * reps, done = 0;
                double hold = Math.Max(5.0, Math.Min(10.0, settleSec * 2.5));
                Random rnd = new Random(7);
                DateTime tStart = DateTime.Now;
                for (int r = 0; r < reps; r++)
                    foreach (MScene f in froms)
                        foreach (int lv in levels)
                        {
                            if (cancel) throw new OperationCanceledException();
                            done++;
                            double per = done > 1 ? (DateTime.Now - tStart).TotalSeconds / (done - 1) : hold + 5;
                            Status(string.Format(CultureInfo.InvariantCulture, "Transitions: {0}/{1}, about {2:0} min left", done, total, per * (total - done + 1) / 60));
                            Show(f);
                            Thread.Sleep((int)((hold - 0.6) * 1000));
                            Row("1f", f, r, -1, Reader.Read(30000));   // the dark scene, settled
                            Thread.Sleep(rnd.Next(0, 300));             // staggers readings across repeats
                            MScene to = f.Copy();
                            to.Id = f.Id + "_to" + lv;
                            to.Patch = lv;
                            Show(to);
                            double tSwitch = clock.Elapsed.TotalSeconds;
                            while (clock.Elapsed.TotalSeconds - tSwitch < 4.0 && !cancel)
                                Row("1", to, r, tSwitch, Reader.Read(30000));
                        }
                report.Add("Transitions run complete.");
            }
            catch (OperationCanceledException) { if (cancel) report.Add("Stopped before the end; what was measured is saved."); }
            catch (Exception ex) { report.Add("Error: " + ex.Message); }
            finally
            {
                sp.Dispose();
                Show(new MScene { Id = "idle", Bg = 16, Patch = -1 });
            }

            // ---- send ----
            string summary = string.Join("\n", report.ToArray());
            if (csv != null && csv.Length > 0)
            {
                csv.Append("# report: ").Append(summary.Replace("\n", " | ")).Append("\n");
                string local = Path.Combine(appDir, "meter_" + stamp + ".csv");
                try { File.WriteAllText(local, csv.ToString()); } catch (Exception) { }
                summary += "\n\nSaved as " + local;
            }
            SetIdle((mode == 0 ? "Test run" : (mode == 1 ? "Full run" : (mode == 2 ? "Transitions run" : (mode == 3 ? "Clip test" : (mode == 4 ? "Frame test" : (mode == 5 ? "Histogram test" : (mode == 7 ? "Fall test" : (mode == 8 ? "Colour test" : "Threshold map")))))))) + " finished.\n\n" + summary + "\n\nT = test run   S = full run   R = transitions   G = clip test   F = frame test   H = histogram test   B = threshold map   D = fall test   K = colour test   Esc = quit");
        }

        // 1.2: DBLE tests. The meter reads the background in the centre (no
        // patch: the background is the probe) while lights elsewhere change.
        // A probe that reads brighter than with a large light on screen means
        // DBLE has lowered its laser and boosted the picture. Run once with
        // DBLE Off (stray light only) and once on High: High / Off = the boost.
        void Probe(bool full, double wait, List<string> report)
        {
            List<MScene> pts = new List<MScene>();
            Func<string, int, double, int, string, int, int, int, int, MScene> L =
                (id, bg, area, level, pos, n, r, gr, b) => new MScene { Id = id, Bg = bg, Patch = -1, BoxArea = area, BoxLevel = level, Lights = area > 0 ? n : 0, LightPos = pos, R = r, G = gr, B = b };
            if (!full)
            {
                pts.Add(L("none", 32, 0, 0, "right", 1, -1, -1, -1));
                pts.Add(L("size0.03", 32, 0.0003, 255, "right", 1, -1, -1, -1));
                pts.Add(L("size1", 32, 0.01, 255, "right", 1, -1, -1, -1));
            }
            else
            {
                // 1 size
                pts.Add(L("none", 32, 0, 0, "right", 1, -1, -1, -1));
                foreach (double a in new double[] { 0.0001, 0.0003, 0.001, 0.003, 0.01, 0.03 })
                    pts.Add(L("size" + (a * 100).ToString(CultureInfo.InvariantCulture), 32, a, 255, "right", 1, -1, -1, -1));
                // 2 level
                foreach (double a in new double[] { 0.001, 0.01 })
                    foreach (int lv in new int[] { 128, 160, 192, 224, 240 })
                        pts.Add(L("level" + lv + "_size" + (a * 100).ToString(CultureInfo.InvariantCulture), 32, a, lv, "right", 1, -1, -1, -1));
                // 3 background
                foreach (int bg in new int[] { 8, 16, 64 })
                    foreach (double a in new double[] { 0, 0.001, 0.01 })
                        pts.Add(L("bg" + bg + "_size" + (a * 100).ToString(CultureInfo.InvariantCulture), bg, a, 255, "right", 1, -1, -1, -1));
                // 4 position
                foreach (string pos in new string[] { "near", "left", "top", "bottom", "corner" })
                    pts.Add(L("pos_" + pos, 32, 0.001, 255, pos, 1, -1, -1, -1));
                // 5 colour
                foreach (double a in new double[] { 0.001, 0.01 })
                {
                    string sz = "_size" + (a * 100).ToString(CultureInfo.InvariantCulture);
                    pts.Add(L("orange" + sz, 32, a, 255, "right", 1, 255, 140, 30));
                    pts.Add(L("red" + sz, 32, a, 255, "right", 1, 255, 30, 20));
                    pts.Add(L("blue" + sz, 32, a, 255, "right", 1, 40, 60, 255));
                }
                // 6 several lights, same total area
                foreach (int n in new int[] { 4, 16 })
                    pts.Add(L("count" + n, 32, 0.004, 255, "right", n, -1, -1, -1));
                pts.Add(L("count1", 32, 0.004, 255, "right", 1, -1, -1, -1));
            }
            int reps = 2, total = pts.Count * reps + (full ? 2 : 0), done = 0;
            double hold = Math.Max(4.0, Math.Min(8.0, settleSec * 2.0));
            DateTime t0 = DateTime.Now;
            Dictionary<string, double> sum = new Dictionary<string, double>();
            Dictionary<string, int> cnt = new Dictionary<string, int>();
            for (int r = 0; r < reps; r++)
                foreach (MScene sc in pts)
                {
                    if (cancel) throw new OperationCanceledException();
                    done++;
                    double per = done > 1 ? (DateTime.Now - t0).TotalSeconds / (done - 1) : hold + 1;
                    Status(string.Format(CultureInfo.InvariantCulture, "DBLE tests: {0}/{1}, about {2:0} min left", done, total, per * (total - done + 1) / 60));
                    Show(sc);
                    Thread.Sleep((int)(hold * 1000));
                    MReading m = Reader.Read(60000);
                    Row("P", sc, r, -1, m);
                    if (m.Ok)
                    {
                        double v; sum[sc.Id] = (sum.TryGetValue(sc.Id, out v) ? v : 0) + m.Y;
                        int c; cnt[sc.Id] = (cnt.TryGetValue(sc.Id, out c) ? c : 0) + 1;
                    }
                }
            // 7 timing: a small light appears on the dark background
            if (full)
                for (int r = 0; r < 2; r++)
                {
                    if (cancel) throw new OperationCanceledException();
                    done++;
                    Status(string.Format(CultureInfo.InvariantCulture, "DBLE tests: {0}/{1}, timing", done, total));
                    Show(L("none", 32, 0, 0, "right", 1, -1, -1, -1));
                    Thread.Sleep((int)(Math.Max(6.0, hold) * 1000));
                    MScene to = L("appear_size0.1", 32, 0.001, 255, "right", 1, -1, -1, -1);
                    Show(to);
                    double tSwitch = clock.Elapsed.TotalSeconds;
                    while (clock.Elapsed.TotalSeconds - tSwitch < 4.0 && !cancel)
                        Row("PT", to, r, tSwitch, Reader.Read(30000));
                }
            StringBuilder sb = new StringBuilder("Centre reading (cd/m2):");
            foreach (MScene sc in pts)
            {
                int c;
                if (cnt.TryGetValue(sc.Id, out c) && c > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.###}", sc.Id, sum[sc.Id] / c);
            }
            report.Add(sb.ToString());
            report.Add(full ? "Full run complete." : "Test run complete.");
        }

        // 1.3: clip test. A light too small for DBLE to count (0.03% of the
        // screen) sits in the centre, under the meter, on a dark background
        // (16), and steps up in level. Each level is held 15 s (going dark is
        // DBLE's slow direction) and read three times. Run with DBLE Off and on
        // High: High / Off rises with the boost, and stops rising where the
        // light clips. The background alone is read too, to take it off.
        // 1.8: G = clip test with colour and size: white, red and orange lights
        // (red / orange like a reactor glow or fire, the level being the strongest
        // channel) at 0.03%, 0.1% and 0.3% of the screen, centred under the meter,
        // on a dark background (16), levels 64-255, each held 10 s and read three
        // times; the background alone at the start and end. Run with the laser
        // dimming off and on: High / Off per light shows where it clips.
        // 1.9: frame test. Loads the chosen log picture and finds its brightest
        // spot: the middle of the pixels within 10% of the brightest channel value.
        void LoadFrame()
        {
            if (frameBmp != null) { frameBmp.Dispose(); frameBmp = null; }
            try
            {
                using (Bitmap src = new Bitmap(frameFiles[frameIdx]))
                    frameBmp = src.Clone(new Rectangle(0, 0, src.Width, src.Height), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                Rectangle r = new Rectangle(0, 0, frameBmp.Width, frameBmp.Height);
                System.Drawing.Imaging.BitmapData bd = frameBmp.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                byte[] px = new byte[bd.Stride * bd.Height];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
                frameBmp.UnlockBits(bd);
                int peak = 0;
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                    {
                        int i = y * bd.Stride + x * 3;
                        peak = Math.Max(peak, Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])));
                    }
                int lim = (int)Math.Ceiling(peak * 0.9);
                double sx = 0, sy = 0; long n = 0;
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                    {
                        int i = y * bd.Stride + x * 3;
                        if (Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) >= lim) { sx += x; sy += y; n++; }
                    }
                framePeak = peak;
                frameX = n > 0 ? (float)(sx / n) : r.Width / 2f;
                frameY = n > 0 ? (float)(sy / n) : r.Height / 2f;
            }
            catch (Exception) { frameBmp = null; }
            scene = new MScene { Id = "frame_pick", Bg = 0, Frame = frameBmp, FrameX = frameX, FrameY = frameY };
        }

        void FrameTest(List<string> report)
        {
            float[] gains = { 1.0f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 1.0f };
            report.Add(string.Format(CultureInfo.InvariantCulture, "Frame: {0}, {1}x{2}, brightest {3}, placed at {4:0},{5:0}",
                Path.GetFileName(frameFiles[frameIdx]), frameBmp.Width, frameBmp.Height, framePeak, frameX, frameY));
            csv.AppendFormat(CultureInfo.InvariantCulture, "# frame: {0} brightest {1} at {2:0},{3:0}\n", Path.GetFileName(frameFiles[frameIdx]), framePeak, frameX, frameY);
            StringBuilder sb = new StringBuilder("Centre reading:");
            for (int j = 0; j < gains.Length; j++)
            {
                if (cancel) throw new OperationCanceledException();
                MScene sc = new MScene { Id = "frame_" + Math.Round(gains[j] * 100).ToString(CultureInfo.InvariantCulture) + (j == gains.Length - 1 ? "_end" : ""),
                    Bg = 0, Patch = -1, Frame = frameBmp, FrameX = frameX, FrameY = frameY, FrameGain = gains[j] };
                Status(string.Format(CultureInfo.InvariantCulture, "Frame test: {0}/{1}", j + 1, gains.Length));
                Show(sc);
                Thread.Sleep(5000);
                double sum = 0; int n = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (cancel) throw new OperationCanceledException();
                    MReading m = Reader.Read(60000);
                    Row("F", sc, k, -1, m);
                    if (m.Ok) { sum += m.Y; n++; }
                }
                if (n > 0) sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.###}", sc.Id, sum / n);
            }
            report.Add(sb.ToString());
            report.Add("Frame test complete.");
        }

        // 1.10: histogram test. The screen is a background level with bright pixels
        // scattered at random (or gathered into one block, or 8 blobs), so the
        // picture's histogram is known exactly. The meter reads a small patch in
        // the centre that the bright pixels never touch.
        //   Part 1: how far the dimming goes for each mix (background 0/16/32/64,
        //           bright share 0-5% at 255, and 128/192 at 0.2% and 1%); patch 96.
        //   Part 2: the same histogram scattered, as one block, as 8 blobs.
        //   Part 3: flares: from the weak scene (patch 96, nothing bright) to the
        //           flare (1% scattered at 255, patch 230) for 0.5, 1, 2, 4 s and
        //           back, three times, the meter reading continuously.
        Bitmap MakeCanvas(int W, int H, int bg, double share, int level, string shape, int seed)
        {
            Bitmap bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            Rectangle r = new Rectangle(0, 0, W, H);
            System.Drawing.Imaging.BitmapData bd = bmp.LockBits(r, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            byte[] px = new byte[bd.Stride * H];
            byte b0 = (byte)bg, b1 = (byte)level;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W * 3; x++) px[y * bd.Stride + x] = b0;
            // Keep clear around the centre patch (and the patch itself).
            double side = Math.Sqrt(HistPatchArea * W * H);
            double clear = side * 3.0;
            Func<int, int, bool> free = (x, y) => Math.Abs(x - W / 2.0) > clear || Math.Abs(y - H / 2.0) > clear;
            long want = (long)Math.Round(share * W * H);
            Random rnd = new Random(seed);
            Action<int, int> set = (x, y) => { int i = y * bd.Stride + x * 3; px[i] = b1; px[i + 1] = b1; px[i + 2] = b1; };
            if (want > 0)
            {
                if (shape == "scatter")
                {
                    long n = 0, guard = 0;
                    while (n < want && guard++ < want * 20)
                    {
                        int x = rnd.Next(W), y = rnd.Next(H), i = y * bd.Stride + x * 3;
                        if (!free(x, y) || px[i] == b1) continue;
                        set(x, y); n++;
                    }
                }
                else
                {
                    int blobs = shape == "block" ? 1 : 8;
                    int s = Math.Max(1, (int)Math.Round(Math.Sqrt((double)want / blobs)));
                    for (int k = 0; k < blobs; k++)
                    {
                        int x0, y0, tries = 0;
                        do
                        {
                            x0 = shape == "block" ? (int)(W * 0.75 - s / 2.0) : rnd.Next(Math.Max(1, W - s));
                            y0 = shape == "block" ? (int)(H / 2.0 - s / 2.0) : rnd.Next(Math.Max(1, H - s));
                        } while (tries++ < 200 && !(free(x0, y0) && free(x0 + s, y0) && free(x0, y0 + s) && free(x0 + s, y0 + s) && free(x0 + s / 2, y0 + s / 2)));
                        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + s); y++)
                            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + s); x++) set(x, y);
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
            bmp.UnlockBits(bd);
            return bmp;
        }

        double HistPatchArea = 0.001;   // 1.11: the test runs at 0.1%, 0.05% and 0.03%

        // 1.12: focused on what the first run showed (2026-11-18, Valerion): one
        // patch size (0.1% did not stop the dimming), no shape part (scattered,
        // block and blobs read the same), no grey 64 or 5% (nothing to learn).
        //   Part 1: the threshold: backgrounds 0, 16, 32; bright share 0 to 1% at
        //           255 in fine steps; bright level 128-224 at a share that counts.
        //   Part 3: flares on 16 and 32, at 0.05%, 0.2% and 1% bright (patch 230),
        //           each from a dimmed scene (nothing bright) and from a scene
        //           already at full laser (1% at 255 elsewhere); 2 s of flare read
        //           continuously, then 2 s back; three times.
        // 1.13: Part 1 runs with the patch at 0.1% and at 0.03% (does the patch move
        // the threshold?); the flares use 0.03% only, so the patch adds little to them.
        void HistogramTest(List<string> report)
        {
            HistPatchArea = 0.001;
            int W = 0, H = 0;
            Invoke((Action)(() => { W = ClientSize.Width; H = ClientSize.Height; }));
            report.Add(string.Format(CultureInfo.InvariantCulture, "Histogram test 1.13: screen {0}x{1}; Part 1 with the patch at 0.1% (p0.1_) and 0.03% (p0.03_), flares at 0.03%.", W, H));
            StringBuilder sb = new StringBuilder("Centre reading:");
            Bitmap last = null;
            Func<string, int, double, int, int, int, MScene> scene = (id, bg, share, level, patch, seed) =>
                new MScene { Id = id, Bg = bg, Patch = patch, PatchArea = HistPatchArea, Canvas = MakeCanvas(W, H, bg, share, level, "scatter", seed) };

            // Part 1: held 6 s, then read three times.
            List<object[]> held = new List<object[]>();
            foreach (double pa in new double[] { 0.001, 0.0003 })
                foreach (int bg in new int[] { 0, 16, 32 })
                {
                    foreach (double sh in new double[] { 0, 0.0001, 0.0002, 0.0003, 0.0005, 0.001, 0.002, 0.005, 0.01 })
                        held.Add(new object[] { bg, sh, 255, pa });
                    foreach (int lv in new int[] { 128, 160, 192, 224 })
                        held.Add(new object[] { bg, bg >= 32 ? 0.01 : 0.002, lv, pa });
                }
            int[] fBg = { 16, 32 };
            double[] fSh = { 0.0005, 0.002, 0.01 };
            int flares = fBg.Length * fSh.Length * 2 * 3;
            int total = held.Count + flares, done = 0;
            foreach (object[] h in held)
            {
                if (cancel) throw new OperationCanceledException();
                done++;
                Status(string.Format(CultureInfo.InvariantCulture, "Histogram test: {0}/{1}", done, total));
                HistPatchArea = (double)h[3];
                string id = string.Format(CultureInfo.InvariantCulture, "p{3}_bg{0}_{1}_l{2}", h[0], ((double)h[1] * 100).ToString(CultureInfo.InvariantCulture), h[2], ((double)h[3] * 100).ToString(CultureInfo.InvariantCulture));
                MScene sc = scene(id, (int)h[0], (double)h[1], (int)h[2], 96, done);
                Show(sc);
                if (last != null) last.Dispose();
                last = sc.Canvas;
                Thread.Sleep(6000);
                double sum = 0; int n = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (cancel) throw new OperationCanceledException();
                    MReading m = Reader.Read(60000);
                    Row("H1", sc, k, -1, m);
                    if (m.Ok) { sum += m.Y; n++; }
                }
                if (n > 0) sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.###}", sc.Id, sum / n);
            }
            report.Add(sb.ToString());

            // Part 3: flares, read continuously (patch 0.03%).
            HistPatchArea = 0.0003;
            StringBuilder fb = new StringBuilder("Flares (patch 230; first read after the switch / mean of reads after 1 s):");
            int fseed = 500;
            for (int r = 0; r < 3; r++)
                foreach (int bg in fBg)
                    foreach (double sh in fSh)
                        foreach (bool dimmed in new bool[] { true, false })
                        {
                            if (cancel) throw new OperationCanceledException();
                            done++;
                            Status(string.Format(CultureInfo.InvariantCulture, "Histogram test: {0}/{1}", done, total));
                            string tag = string.Format(CultureInfo.InvariantCulture, "bg{0}_{1}_{2}", bg, (sh * 100).ToString(CultureInfo.InvariantCulture), dimmed ? "dim" : "full");
                            // Before: nothing bright (EBL dims) or 1% at 255 already (laser at full).
                            MScene before = scene("pre_" + tag, bg, dimmed ? 0 : 0.01, 255, 96, fseed++);
                            // Flare: the same, plus sh bright and the patch at 230.
                            MScene flare = scene("flare_" + tag, bg, (dimmed ? 0 : 0.01) + sh, 255, 230, fseed++);
                            Show(before);
                            if (last != null) last.Dispose();
                            last = null;
                            Thread.Sleep(6000);
                            Row("H3w", before, r, -1, Reader.Read(30000));
                            Show(flare);
                            before.Canvas.Dispose();
                            double tSwitch = clock.Elapsed.TotalSeconds;
                            double first = -1, sumL = 0; int nL = 0;
                            while (clock.Elapsed.TotalSeconds - tSwitch < 2.0 && !cancel)
                            {
                                double t0 = clock.Elapsed.TotalSeconds - tSwitch;
                                MReading m = Reader.Read(30000);
                                Row("H3", flare, r, tSwitch, m);
                                if (m.Ok) { if (first < 0) first = m.Y; if (t0 >= 1.0) { sumL += m.Y; nL++; } }
                            }
                            MScene back = scene("back_" + tag, bg, dimmed ? 0 : 0.01, 255, 96, fseed++);
                            Show(back);
                            flare.Canvas.Dispose();
                            last = back.Canvas;
                            double tBack = clock.Elapsed.TotalSeconds;
                            while (clock.Elapsed.TotalSeconds - tBack < 2.0 && !cancel)
                                Row("H3b", back, r, tBack, Reader.Read(30000));
                            fb.AppendFormat(CultureInfo.InvariantCulture, "  {0}: {1:0.#} / {2:0.#}", tag, first, nL > 0 ? sumL / nL : -1);
                        }
            report.Add(fb.ToString());
            Show(new MScene { Id = "end", Bg = 16, Patch = -1, Canvas = MakeCanvas(W, H, 16, 0, 255, "scatter", 999) });
            if (last != null) last.Dispose();
            report.Add("Histogram test complete.");
        }

        // 1.14: threshold map (B). Fills in what H showed only for backgrounds 16
        // and 32: the bright share (scattered pixels at 255) that keeps the laser
        // up, on backgrounds 8, 24, 40, 48 and 64. Patch 0.03% at level 96 in the
        // centre, held 6 s, read three times. No text on screen.
        void ThresholdMap(List<string> report)
        {
            HistPatchArea = 0.0003;
            int W = 0, H = 0;
            Invoke((Action)(() => { W = ClientSize.Width; H = ClientSize.Height; }));
            report.Add(string.Format(CultureInfo.InvariantCulture, "Threshold map 1.14: screen {0}x{1}, patch 0.03% at 96.", W, H));
            StringBuilder sb = new StringBuilder("Centre reading:");
            int[] bgs = { 8, 24, 40, 48, 64 };
            double[] shares = { 0, 0.0001, 0.0003, 0.0005, 0.001, 0.002, 0.003, 0.005, 0.01, 0.02 };
            int total = bgs.Length * shares.Length, done = 0;
            Bitmap last = null;
            foreach (int bg in bgs)
                foreach (double sh in shares)
                {
                    if (cancel) throw new OperationCanceledException();
                    done++;
                    Status(string.Format(CultureInfo.InvariantCulture, "Threshold map: {0}/{1}", done, total));
                    MScene sc = new MScene { Id = string.Format(CultureInfo.InvariantCulture, "bg{0}_{1}_l255", bg, (sh * 100).ToString(CultureInfo.InvariantCulture)),
                        Bg = bg, Patch = 96, PatchArea = HistPatchArea, Canvas = MakeCanvas(W, H, bg, sh, 255, "scatter", 2000 + done) };
                    Show(sc);
                    if (last != null) last.Dispose();
                    last = sc.Canvas;
                    Thread.Sleep(6000);
                    double sum = 0; int n = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        if (cancel) throw new OperationCanceledException();
                        MReading m = Reader.Read(60000);
                        Row("B", sc, k, -1, m);
                        if (m.Ok) { sum += m.Y; n++; }
                    }
                    if (n > 0) sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.###}", sc.Id, sum / n);
                }
            report.Add(sb.ToString());
            Show(new MScene { Id = "end", Bg = 16, Patch = -1, Canvas = MakeCanvas(W, H, 16, 0, 255, "scatter", 999) });
            if (last != null) last.Dispose();
            report.Add("Threshold map complete.");
        }

        // 1.15: fall test (D). How fast the laser dims after a bright scene cuts to a dark one.
        // A full bright screen (laser up) for 5 s, then black with a small white highlight
        // (0.03% of the screen, level 192: what DBLE clips) at the meter, read for 8 s while
        // the laser comes down: the highlight starts unclipped and drops to its clipped level.
        // From full white and from full mid-grey, 3 times each.
        void FallTest(List<string> report)
        {
            MScene[] froms = {
                new MScene { Id = "fall_from_white", Bg = 255, Patch = 255 },
                new MScene { Id = "fall_from_mid", Bg = 128, Patch = 128 } };
            const int reps = 3;
            int total = froms.Length * reps, done = 0;
            DateTime t0 = DateTime.Now;
            StringBuilder sb = new StringBuilder("Fall (s after the cut; highlight cd/m2):");
            foreach (MScene f in froms)
            {
                List<double> t50 = new List<double>(), t90 = new List<double>();
                double firstSum = 0, finalSum = 0; int nTry = 0;
                for (int r = 0; r < reps; r++)
                {
                    if (cancel) throw new OperationCanceledException();
                    done++;
                    double per = done > 1 ? (DateTime.Now - t0).TotalSeconds / (done - 1) : 14;
                    Status(string.Format(CultureInfo.InvariantCulture, "Fall test: {0}/{1}, about {2:0.0} min left", done, total, per * (total - done + 1) / 60));
                    Show(f);
                    Thread.Sleep(4500);
                    Row("Df", f, r, -1, Reader.Read(30000));   // the bright scene, laser up
                    MScene to = new MScene { Id = f.Id + "_to_spot192", Bg = 0, Patch = -1, FR = 192, FG = 192, FB = 192, Window = 0.0003 };
                    Show(to);
                    double tSwitch = clock.Elapsed.TotalSeconds;
                    List<double[]> pts = new List<double[]>();
                    while (clock.Elapsed.TotalSeconds - tSwitch < 8.0 && !cancel)
                    {
                        MReading m = Reader.Read(30000);
                        Row("D", to, r, tSwitch, m);
                        if (m.Ok) pts.Add(new double[] { (m.T0 + m.T1) / 2 - tSwitch, m.Y });
                    }
                    if (pts.Count >= 4)
                    {
                        double first = pts[0][1], fin = 0;
                        for (int i = pts.Count - 3; i < pts.Count; i++) fin += pts[i][1];
                        fin /= 3;
                        firstSum += first; finalSum += fin; nTry++;
                        double drop = first - fin;
                        if (drop > first * 0.05)
                        {
                            foreach (double[] p in pts) if (p[1] <= first - drop * 0.5) { t50.Add(p[0]); break; }
                            foreach (double[] p in pts) if (p[1] <= first - drop * 0.9) { t90.Add(p[0]); break; }
                        }
                    }
                }
                if (nTry > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "  {0}: first {1:0.#} final {2:0.#}", f.Id, firstSum / nTry, finalSum / nTry);
                    if (t50.Count > 0) sb.AppendFormat(CultureInfo.InvariantCulture, " half {0:0.00}", Avg(t50));
                    if (t90.Count > 0) sb.AppendFormat(CultureInfo.InvariantCulture, " 90% {0:0.00}", Avg(t90));
                    if (t50.Count == 0) sb.Append(" no fall seen");
                }
            }
            report.Add(sb.ToString());
            report.Add("Fall test complete.");
        }

        // 1.16: colour test (K). The dimming is set by a control light at the right of the
        // screen (DBLE goes by the brightest 0.05%): none, 0.1% at 128 / 160 / 192 / 224, or
        // 1% at 255 (laser up). The meter reads small patches (0.03%, too small to count)
        // at the centre on a level-16 background, so the patch never changes the dimming:
        //  A  greys 32-224 under every dimming level (96 is the reference for the boost),
        //  B  red, green, blue, cyan, magenta, yellow, orange and a skin tone at 128 under
        //     no light, light 192 and laser up, and at 224 with no light,
        //  C  red, green, blue and orange at 160 / 192 / 255 with no light (each colour's clip).
        // Run once each on DBLE Off, Low and High; the shifts are the differences.
        void ColourTest(List<string> report)
        {
            const int bg = 16;
            const double win = 0.0003;
            string[] stName = { "none", "ctl128", "ctl160", "ctl192", "ctl224", "up255" };
            int[] stLevel = { 0, 128, 160, 192, 224, 255 };
            double[] stArea = { 0, 0.001, 0.001, 0.001, 0.001, 0.01 };
            string[] cn = { "red", "green", "blue", "cyan", "magenta", "yellow", "orange", "skin" };
            double[][] cm = {
                new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }, new double[] { 0, 1, 1 },
                new double[] { 1, 0, 1 }, new double[] { 1, 1, 0 }, new double[] { 1, 0.55, 0.12 }, new double[] { 1, 0.77, 0.63 } };
            // The points, grouped by dimming level so the dimming changes only between groups.
            List<KeyValuePair<int, MScene>> pts = new List<KeyValuePair<int, MScene>>();
            Func<int, string, int, int, int, MScene> mk = (st, id, r, g2, b) => new MScene {
                Id = stName[st] + "_" + id, Bg = bg, Patch = -1, FR = r, FG = g2, FB = b, Window = win, NoText = true,
                Lights = stArea[st] > 0 ? 1 : 0, BoxArea = stArea[st], BoxLevel = stLevel[st], LightPos = "right" };
            for (int st = 0; st < stName.Length; st++)
            {
                foreach (int lv in new int[] { 96, 32, 64, 128, 160, 192, 224 })
                    pts.Add(new KeyValuePair<int, MScene>(st, mk(st, "grey" + lv, lv, lv, lv)));
                if (st == 0 || st == 3 || st == 5)
                    for (int c = 0; c < cn.Length; c++)
                        pts.Add(new KeyValuePair<int, MScene>(st, mk(st, cn[c] + "128", (int)Math.Round(128 * cm[c][0]), (int)Math.Round(128 * cm[c][1]), (int)Math.Round(128 * cm[c][2]))));
                if (st == 0)
                {
                    for (int c = 0; c < cn.Length; c++)
                        pts.Add(new KeyValuePair<int, MScene>(st, mk(st, cn[c] + "224", (int)Math.Round(224 * cm[c][0]), (int)Math.Round(224 * cm[c][1]), (int)Math.Round(224 * cm[c][2]))));
                    foreach (int c in new int[] { 0, 1, 2, 6 })
                        foreach (int lv in new int[] { 160, 192, 255 })
                            pts.Add(new KeyValuePair<int, MScene>(st, mk(st, cn[c] + lv, (int)Math.Round(lv * cm[c][0]), (int)Math.Round(lv * cm[c][1]), (int)Math.Round(lv * cm[c][2]))));
                }
            }
            int total = pts.Count, done = 0, lastSt = -1;
            DateTime t0 = DateTime.Now;
            StringBuilder sb = new StringBuilder("Colour (x,y,Y):");
            foreach (KeyValuePair<int, MScene> p in pts)
            {
                if (cancel) throw new OperationCanceledException();
                done++;
                double per = done > 1 ? (DateTime.Now - t0).TotalSeconds / (done - 1) : 5;
                Status(string.Format(CultureInfo.InvariantCulture, "Colour test: {0}/{1}, about {2:0} min left", done, total, per * (total - done + 1) / 60 + 1.5));
                Show(p.Value);
                // A new dimming level needs the projector to settle (Low rises in about 2.6 s);
                // within a level only the small patch changes.
                Thread.Sleep(p.Key != lastSt ? 5000 : 1500);
                lastSt = p.Key;
                double sx = 0, sy = 0, sz = 0; int n = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (cancel) throw new OperationCanceledException();
                    MReading m = Reader.Read(60000);
                    Row("K", p.Value, k, -1, m);
                    if (m.Ok) { sx += m.X; sy += m.Y; sz += m.Z; n++; }
                }
                double sum = sx + sy + sz;
                if (n > 0 && sum > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.0000},{2:0.0000},{3:0.###}", p.Value.Id, sx / sum, sy / sum, sy / n);
            }
            report.Add(sb.ToString());
            report.Add("Colour test complete (" + total + " points).");
        }

        static double Avg(List<double> l) { double s = 0; foreach (double v in l) s += v; return l.Count > 0 ? s / l.Count : 0; }

        void ClipTestColour(List<string> report)
        {
            const int bg = 16;
            double[] sizes = { 0.0003, 0.001, 0.003 };
            string[] cname = { "white", "red", "orange" };
            double[][] cmul = { new double[] { 1, 1, 1 }, new double[] { 1, 0.15, 0.10 }, new double[] { 1, 0.55, 0.12 } };
            int[] levels = { 64, 128, 160, 192, 224, 255 };
            List<MScene> pts = new List<MScene>();
            pts.Add(new MScene { Id = "bg_only", Bg = bg, Patch = -1 });
            foreach (double sz in sizes)
                for (int c = 0; c < cname.Length; c++)
                    foreach (int lv in levels)
                        pts.Add(new MScene { Id = cname[c] + "_" + (sz * 100).ToString(CultureInfo.InvariantCulture) + "_" + lv, Bg = bg, Patch = -1,
                            FR = (int)Math.Round(lv * cmul[c][0]), FG = (int)Math.Round(lv * cmul[c][1]), FB = (int)Math.Round(lv * cmul[c][2]), Window = sz });
            pts.Add(new MScene { Id = "bg_only_end", Bg = bg, Patch = -1 });
            int total = pts.Count, done = 0;
            DateTime t0 = DateTime.Now;
            StringBuilder sb = new StringBuilder("Centre reading:");
            foreach (MScene sc in pts)
            {
                if (cancel) throw new OperationCanceledException();
                done++;
                double per = done > 1 ? (DateTime.Now - t0).TotalSeconds / (done - 1) : 12;
                Status(string.Format(CultureInfo.InvariantCulture, "Clip test: {0}/{1}, about {2:0} min left", done, total, per * (total - done + 1) / 60));
                Show(sc);
                Thread.Sleep(10000);
                double sum = 0; int n = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (cancel) throw new OperationCanceledException();
                    MReading m = Reader.Read(60000);
                    Row("G", sc, k, -1, m);
                    if (m.Ok) { sum += m.Y; n++; }
                }
                if (n > 0) sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.###}", sc.Id, sum / n);
            }
            report.Add(sb.ToString());
            report.Add("Clip test complete.");
        }

        void ClipTest(List<string> report)
        {
            const int bg = 16;
            const double area = 0.0003;
            int[] levels = { -1, 64, 96, 128, 160, 192, 208, 224, 240, 255 };
            int reps = 2, total = levels.Length * reps, done = 0;
            DateTime t0 = DateTime.Now;
            Dictionary<int, double> sum = new Dictionary<int, double>();
            Dictionary<int, int> cnt = new Dictionary<int, int>();
            for (int r = 0; r < reps; r++)
                foreach (int lv in levels)
                {
                    if (cancel) throw new OperationCanceledException();
                    done++;
                    double per = done > 1 ? (DateTime.Now - t0).TotalSeconds / (done - 1) : 17;
                    Status(string.Format(CultureInfo.InvariantCulture, "Clip test: {0}/{1}, about {2:0} min left", done, total, per * (total - done + 1) / 60));
                    MScene sc = new MScene { Id = lv < 0 ? "bg_only" : "light" + lv, Bg = bg, Patch = lv, PatchArea = lv < 0 ? 0 : area };
                    Show(sc);
                    Thread.Sleep(15000);
                    for (int k = 0; k < 3; k++)
                    {
                        if (cancel) throw new OperationCanceledException();
                        MReading m = Reader.Read(60000);
                        Row("G", sc, r, -1, m);
                        if (m.Ok)
                        {
                            double v; sum[lv] = (sum.TryGetValue(lv, out v) ? v : 0) + m.Y;
                            int c; cnt[lv] = (cnt.TryGetValue(lv, out c) ? c : 0) + 1;
                        }
                    }
                }
            StringBuilder sb = new StringBuilder("Centre reading (cd/m2):");
            foreach (int lv in levels)
            {
                int c;
                if (cnt.TryGetValue(lv, out c) && c > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "  {0} {1:0.####}", lv < 0 ? "bg" : lv.ToString(CultureInfo.InvariantCulture), sum[lv] / c);
            }
            report.Add(sb.ToString());
            report.Add("Clip test complete.");
        }

        // Shows a scene, waits for the dimming to settle, reads once.
        MReading Settled(string step, MScene sc, double waitSec)
        {
            Show(sc);
            Thread.Sleep((int)(waitSec * 1000));
            MReading m = Reader.Read(60000);
            Row(step, sc, 0, -1, m);
            return m;
        }

        Spotread reader;
        Spotread Reader { get { return reader; } }

        // From black to a small white patch: read as fast as possible for 12 s;
        // settled = from when every reading stays within 2% of the last 2 s.
        double SettleTime()
        {
            MScene from = new MScene { Id = "settle_from", Bg = 0, Patch = 0 };
            MScene to = new MScene { Id = "settle_to", Bg = 0, Patch = 255 };
            Show(from);
            Thread.Sleep(5000);
            Show(to);
            double tSwitch = clock.Elapsed.TotalSeconds;
            List<MReading> rs = new List<MReading>();
            while (clock.Elapsed.TotalSeconds - tSwitch < 12.0 && !cancel)
            {
                MReading m = Reader.Read(30000);
                Row("0s", to, 0, tSwitch, m);
                if (m.Ok) rs.Add(m);
            }
            if (rs.Count < 3) return 3.0;
            double tEnd = rs[rs.Count - 1].T1, sum = 0; int n = 0;
            foreach (MReading m in rs) if (m.T1 >= tEnd - 2.0) { sum += m.Y; n++; }
            double fin = sum / Math.Max(1, n);
            double settled = 0;
            for (int i = rs.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(rs[i].Y - fin) > 0.02 * Math.Max(fin, 1e-6)) { settled = (i + 1 < rs.Count ? rs[i + 1].T0 : rs[i].T1) - tSwitch; break; }
            }
            return Math.Max(0.3, settled);
        }
    }
}
