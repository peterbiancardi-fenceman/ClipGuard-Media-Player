// ClipGuard 2.53.0 launcher (formerly DBLEFix; no window; drives the
// renderer's on-screen overlay). Built three times from this file:
//   ClipGuard       public, for any PC
//   ClipGuard+      public, /define:PLUS: for higher-end PCs: adds High
//                   Performance look-ahead and the Upscaler (FSRCNNX)
//   ClipGuard Media Player  /define:TEST;PLAYER;STANDALONE;PUBLIC
//
// Unplugged loads its renderer from <folder of Unplugged.exe>\filters, so the
// ClipGuard build has to run from a folder of its own. This program keeps one
// under %LOCALAPPDATA%\ClipGuard\Unplugged: the top-level files and the filters
// folder are copied from the installed Unplugged, every other folder is a
// junction (a link, no copy), and filters\unplugged_vr.ax is replaced with the
// ClipGuard build. The copy is refreshed when Unplugged or the renderer
// changes. The installed Unplugged is only read, never changed.
//
// After starting Unplugged it stays running in the background (no window): it
// shares the settings with the renderer through shared memory and registers
// the shortcuts (F7-F11, arrows) that drive the renderer's on-screen overlay.
// Settings are saved to filters\clipguard.ini in the copy. It exits with
// Unplugged.
//
// F11 saves a log (the last 30 s of readout numbers and a screenshot) and
// opens its folder.
//
//
// Written for C# 5 so it builds with the csc.exe that ships with Windows.

#if TEST
#define PLUS
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClipGuard
{
    static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetPrivateProfileString(string section, string key, string def,
            StringBuilder ret, int size, string file);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static string ReadIni(string file, string section, string key, string def)
        {
            StringBuilder sb = new StringBuilder(512);
            GetPrivateProfileString(section, key, def, sb, sb.Capacity, file);
            return sb.ToString();
        }
    }

    // ClipGuard or ClipGuard+ (2.44.0).
    static class Edition
    {
#if PLUS
        public const bool Plus = true;
        public const string Name = "ClipGuard+";
#else
        public const bool Plus = false;
        public const string Name = "ClipGuard";
#endif
    }

    static class Program
    {
        const string Title = Edition.Name;

        [STAThread]
#if TEST
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
#endif

        static int Main(string[] args)
        {
#if TEST
            // 2.52.1: the Media Player draws at the screen's real pixels (Windows scaled it up and blurred it).
            try { SetProcessDPIAware(); } catch (Exception) { }
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static Mutex instanceMutex;

        static int Run(string[] args)
        {
            // Started by "Restart & update": wait for the old ClipGuard to finish.
            bool updated = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "/updated") updated = true;
                if (args[i] == "/restart" && i + 1 < args.Length)
                {
                    int pid;
                    if (int.TryParse(args[i + 1], out pid))
                    {
                        try { Process.GetProcessById(pid).WaitForExit(15000); } catch (Exception) { }
                    }
                }
            }

            // One ClipGuard at a time. Running it again brings the overlay back.
            bool createdNew;
            instanceMutex = new Mutex(true, @"Local\ClipGuard_exe", out createdNew);
            if (!createdNew)
            {
                Native.PostMessage((IntPtr)0xFFFF, ControlForm.ShowMeMessage, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string ourAx = Path.Combine(appDir, "unplugged_vr.ax");
            string seedIni = Path.Combine(appDir, "clipguard.ini");
            if (!File.Exists(ourAx))
                throw new Exception("unplugged_vr.ax is missing. Keep it in the same folder as ClipGuard.exe.");

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string workRoot = Path.Combine(localApp, "ClipGuard", "Unplugged");
            string liveIni = Path.Combine(workRoot, "filters", "clipguard.ini");

            // Unplugged already running: if it is our copy (ClipGuard was closed), attach to it.
            Process[] running = Process.GetProcessesByName("Unplugged");
            if (running.Length > 0)
            {
                bool ours = false;
                foreach (Process p in running)
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (path.StartsWith(workRoot, StringComparison.OrdinalIgnoreCase)) ours = true;
                    }
                    catch (Exception) { }
                }
                if (!ours)
                {
                    MessageBox.Show("Unplugged is already running. Close it, then start ClipGuard again.",
                        Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                if (!File.Exists(liveIni)) SeedIni(seedIni, liveIni);
                Application.Run(new ControlForm(liveIni, seedIni));
                return 0;
            }


            string srcDir = FindUnplugged(seedIni);
            if (srcDir == null)
                throw new Exception("Could not find Unplugged.exe.\n\nOpen clipguard.ini next to ClipGuard.exe and set " +
                                    "UnpluggedExe= under [Launcher] to its full path.");

            string stamp = BuildStamp(srcDir, ourAx);
            string stampFile = Path.Combine(workRoot, ".clipguard_stamp");
            bool upToDate = File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp
                            && File.Exists(Path.Combine(workRoot, "Unplugged.exe"))
                            && File.Exists(Path.Combine(workRoot, "filters", "unplugged_vr.ax"));
            if (!upToDate)
            {
                Cursor.Current = Cursors.WaitCursor;
                Prepare(srcDir, workRoot, ourAx, seedIni);
                File.WriteAllText(stampFile, stamp);
                Cursor.Current = Cursors.Default;
            }

            if (!File.Exists(liveIni))
                SeedIni(seedIni, liveIni);
            RestoreLuts(appDir, workRoot);

            // Shared memory and shortcuts are up before Unplugged starts.
            ControlForm form = new ControlForm(liveIni, seedIni);

            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(workRoot, "Unplugged.exe"));
            psi.WorkingDirectory = workRoot;
            psi.UseShellExecute = false;
            Process.Start(psi);

            Application.Run(form);
            return 0;
        }

#if PLAYER
        // 2.56.0: the player's start does what ClipGuard's does before Unplugged runs:
        // ClipGuard's copy of Unplugged gets the renderer next to ClipGuard.exe when
        // either has changed (so an update the player installed takes effect at once).
        internal static void RefreshCopy(string appDir, string workRoot)
        {
            string ourAx = Path.Combine(appDir, "unplugged_vr.ax");
            string seedIni = Path.Combine(appDir, "clipguard.ini");
            if (!File.Exists(ourAx)) return;
            string srcDir = FindUnplugged(seedIni);
            if (srcDir == null) return;
            string stamp = BuildStamp(srcDir, ourAx);
            string stampFile = Path.Combine(workRoot, ".clipguard_stamp");
            bool upToDate = File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp
                            && File.Exists(Path.Combine(workRoot, "Unplugged.exe"))
                            && File.Exists(Path.Combine(workRoot, "filters", "unplugged_vr.ax"));
            if (!upToDate)
            {
                Cursor.Current = Cursors.WaitCursor;
                Prepare(srcDir, workRoot, ourAx, seedIni);
                File.WriteAllText(stampFile, stamp);
                Cursor.Current = Cursors.Default;
            }
            string liveIni = Path.Combine(workRoot, "filters", "clipguard.ini");
            if (!File.Exists(liveIni)) SeedIni(seedIni, liveIni);
            RestoreLuts(appDir, workRoot);
        }
#endif

        // ---- finding the installed Unplugged ------------------------------------

        static string FindUnplugged(string seedIni)
        {
            if (File.Exists(seedIni))
            {
                string p = Native.ReadIni(seedIni, "Launcher", "UnpluggedExe", "").Trim().Trim('"');
                if (p.Length > 0 && File.Exists(p))
                    return Path.GetDirectoryName(Path.GetFullPath(p));
            }

            RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey un = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (un == null) continue;
                            foreach (string name in un.GetSubKeyNames())
                            {
                                using (RegistryKey k = un.OpenSubKey(name))
                                {
                                    if (k == null) continue;
                                    string dn = k.GetValue("DisplayName") as string;
                                    if (dn == null || !dn.StartsWith("Unplugged", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    string loc = (k.GetValue("InstallLocation") as string) ?? "";
                                    loc = loc.Trim().Trim('"');
                                    if (loc.Length > 0 && File.Exists(Path.Combine(loc, "Unplugged.exe")))
                                        return loc.TrimEnd('\\');
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }

            string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unplugged");
            if (File.Exists(Path.Combine(pf, "Unplugged.exe")))
                return pf;
            return null;
        }

        // ---- keeping the private copy -------------------------------------------

        static string BuildStamp(string srcDir, string ourAx)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("src=").Append(srcDir.ToLowerInvariant()).Append('\n');
            AppendFiles(sb, srcDir);
            string filters = Path.Combine(srcDir, "filters");
            if (Directory.Exists(filters)) AppendFiles(sb, filters);
            FileInfo ax = new FileInfo(ourAx);
            sb.Append("ax|").Append(ax.Length).Append('|').Append(ax.LastWriteTimeUtc.Ticks).Append('\n');
            return sb.ToString();
        }

        static void AppendFiles(StringBuilder sb, string dir)
        {
            List<string> files = new List<string>(Directory.GetFiles(dir));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                FileInfo fi = new FileInfo(f);
                sb.Append(fi.Name.ToLowerInvariant()).Append('|').Append(fi.Length).Append('|')
                  .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }

        static void Prepare(string srcDir, string workRoot, string ourAx, string seedIni)
        {
            Directory.CreateDirectory(workRoot);

            // Keep the user's settings across a refresh.
            string liveIni = Path.Combine(workRoot, "filters", "clipguard.ini");
            string keptIni = File.Exists(liveIni) ? File.ReadAllText(liveIni) : null;
            // 2.48.3: and the calibration LUTs (made by ClipGuard Meter's auto-cal).
            Dictionary<string, byte[]> keptLuts = new Dictionary<string, byte[]>();
            foreach (string n in LutFiles)
            {
                string f = Path.Combine(workRoot, "filters", n);
                try { if (File.Exists(f)) keptLuts[n] = File.ReadAllBytes(f); } catch (Exception) { }
            }

            ClearFolder(workRoot);

            foreach (string f in Directory.GetFiles(srcDir))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) continue;
                CopyFile(f, Path.Combine(workRoot, name));
            }

            foreach (string d in Directory.GetDirectories(srcDir))
            {
                string name = Path.GetFileName(d);
                string dst = Path.Combine(workRoot, name);
                if (string.Equals(name, "filters", StringComparison.OrdinalIgnoreCase))
                    CopyTree(d, dst);
                else if (!MakeJunction(dst, d))
                    CopyTree(d, dst);
            }

            string filtersDst = Path.Combine(workRoot, "filters");
            Directory.CreateDirectory(filtersDst);
            CopyFile(ourAx, Path.Combine(filtersDst, "unplugged_vr.ax"));

            if (keptIni != null)
                File.WriteAllText(liveIni, keptIni);
            else
                SeedIni(seedIni, liveIni);
            foreach (KeyValuePair<string, byte[]> kv in keptLuts)
                File.WriteAllBytes(Path.Combine(filtersDst, kv.Key), kv.Value);
        }

        static readonly string[] LutFiles = { "titan.cube", "titan.txt", "valerion.cube", "valerion.txt" };

        // 2.48.3: a LUT missing from the renderer's folder is restored from the copy
        // ClipGuard Meter keeps next to ClipGuard.exe. An existing one is never touched.
        static void RestoreLuts(string appDir, string workRoot)
        {
            string filters = Path.Combine(workRoot, "filters");
            if (!Directory.Exists(filters)) return;
            foreach (string n in LutFiles)
            {
                string src = Path.Combine(appDir, n), dst = Path.Combine(filters, n);
                try { if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst); } catch (Exception) { }
            }
        }

        static void SeedIni(string seedIni, string liveIni)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(liveIni));
            if (File.Exists(seedIni))
                File.Copy(seedIni, liveIni, true);
            else
                File.WriteAllText(liveIni,
                    "[ClipGuard]\r\nEnabled=1\r\nStrength=0.35\r\nFixStartPct=10.0\r\nFullEffectPct=3.0\r\nHighlightStart=0.40\r\nColorProtect=0.90\r\nTextSkipPct=25\r\nHotSpot=0.60\r\nHotSpotBelowPct=3.0\r\nFixShape=1\r\nGradedSpot=0.50\r\nHotSpotMask=1\r\nDimFire=0\r\nUpscaler=0\r\nDither=0\r\nProjector=0\r\nLut=0\r\nGrid=1\r\nLookAhead=4\r\nView=0\r\nOverlayPinned=0\r\n");
        }

        // Empties the folder without ever following a link: a junction is
        // removed as a link, so the installed Unplugged it points at is safe.
        static void ClearFolder(string dir)
        {
            foreach (string d in Directory.GetDirectories(dir))
            {
                DirectoryInfo di = new DirectoryInfo(d);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(d, false);
                else
                {
                    ClearFolder(d);
                    di.Attributes = FileAttributes.Normal;
                    Directory.Delete(d, false);
                }
            }
            foreach (string f in Directory.GetFiles(dir))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
        }

        static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                CopyFile(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (string d in Directory.GetDirectories(src))
                CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        static void CopyFile(string src, string dst)
        {
            File.Copy(src, dst, true);
            File.SetAttributes(dst, FileAttributes.Normal);
        }

        static bool MakeJunction(string link, string target)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c mklink /J \"" + link + "\" \"" + target + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 && Directory.Exists(link);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // 2.92.0: the graphics card's output range (Full / Limited), as NVIDIA's control panel sets
    // it, read through NVAPI's colour control for the display the player is on. 0 when there is
    // no NVIDIA driver or it can't be read, so the banner says nothing rather than guess.
    static class NvRange
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdByNameFn([MarshalAs(UnmanagedType.LPStr)] string name, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ColorFn(uint id, IntPtr data);
        static bool tried, ok;
        static IdByNameFn idByName;
        static ColorFn color;

        public static int Read()
        {
            try
            {
                if (!tried)
                {
                    tried = true;
                    IntPtr pi = QueryInterface(0x0150E828), pn = QueryInterface(0xAE457190), pc = QueryInterface(0x92F9D80D);
                    if (pi == IntPtr.Zero || pn == IntPtr.Zero || pc == IntPtr.Zero) return 0;
                    if (((InitFn)Marshal.GetDelegateForFunctionPointer(pi, typeof(InitFn)))() != 0) return 0;
                    idByName = (IdByNameFn)Marshal.GetDelegateForFunctionPointer(pn, typeof(IdByNameFn));
                    color = (ColorFn)Marshal.GetDelegateForFunctionPointer(pc, typeof(ColorFn));
                    ok = true;
                }
                if (!ok) return 0;
                Screen sc = null;
                try { IntPtr w = Native.GetForegroundWindow(); if (w != IntPtr.Zero) sc = Screen.FromHandle(w); } catch { }
                if (sc == null) sc = Screen.PrimaryScreen;
                uint id;
                if (idByName(sc.DeviceName, out id) != 0) return 0;
                // NV_COLOR_DATA v5 (24 bytes), then v4 (20): cmd 1 = get; data.dynamicRange at byte 10:
                // 0 = VESA (full), 1 = CEA (limited)
                foreach (int[] v in new[] { new[] { 5, 24 }, new[] { 4, 20 } })
                {
                    IntPtr buf = Marshal.AllocHGlobal(32);
                    try
                    {
                        for (int i = 0; i < 32; i++) Marshal.WriteByte(buf, i, 0);
                        Marshal.WriteInt32(buf, 0, v[1] | (v[0] << 16));
                        Marshal.WriteInt16(buf, 4, (short)v[1]);
                        Marshal.WriteByte(buf, 6, 1);
                        if (color(id, buf) != 0) continue;
                        int dr = Marshal.ReadByte(buf, 10);
                        return dr == 0 ? 1 : dr == 1 ? 2 : 0;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch (Exception) { ok = false; }
            return 0;
        }

        // 0.9.6: the BT.2020 flag only: the colorimetry byte (9) of the card's current colour
        // data goes to BT.2020 (8 for RGB, 9 for YCbCr); format, depth, range and the user's
        // choice are written back exactly as read. The picture is not touched: the display is
        // only told the signal's colours are BT.2020, as madVR does. False when refused.
        public static bool SendBt2020(string device) { return SendColorimetry(device, true); }
        // 0.9.6: the flag follows Output gamut: BT.2020 (8 RGB / 9 YCbCr) or BT.709 (0 RGB / 2 YCbCr)
        public static bool SendColorimetry(string device, bool bt2020)
        {
            try
            {
                if (!tried) Read();
                if (!ok) return true;
                uint id;
                if (idByName(device, out id) != 0) return true;
                foreach (int[] v in new[] { new[] { 5, 24 }, new[] { 4, 20 } })
                {
                    IntPtr buf = Marshal.AllocHGlobal(32);
                    try
                    {
                        for (int i = 0; i < 32; i++) Marshal.WriteByte(buf, i, 0);
                        Marshal.WriteInt32(buf, 0, v[1] | (v[0] << 16));
                        Marshal.WriteInt16(buf, 4, (short)v[1]);
                        Marshal.WriteByte(buf, 6, 1);       // get
                        if (color(id, buf) != 0) continue;
                        bool rgb = Marshal.ReadByte(buf, 8) == 0;
                        byte want = (byte)(bt2020 ? (rgb ? 8 : 9) : (rgb ? 0 : 2));
                        if (Marshal.ReadByte(buf, 9) == want) return true;
                        Marshal.WriteInt32(buf, 0, v[1] | (v[0] << 16));
                        Marshal.WriteInt16(buf, 4, (short)v[1]);
                        Marshal.WriteByte(buf, 6, 2);       // set, everything else as read
                        Marshal.WriteByte(buf, 9, want);
                        return color(id, buf) == 0;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch (Exception) { }
            return true;
        }
    }

    // ---- shared memory with the renderer --------------------------------------
    // Mirrors ClipGuardShared in ClipGuard.h (Pack = 4, 256 bytes). Offsets in bytes.

    class Shared : IDisposable
    {
        public const uint Magic = 0x454C4244; // "DBLE" (kept: the renderer checks it)
        const int OMagic = 0, OVersion = 4, OEnabled = 8, OStrength = 12, OTrigger = 16, OKnee = 20, OView = 24,
            OOverlayOn = 28, OPinned = 32, OSelected = 36, OLastKey = 40, OExeTick = 44,
            ORendTick = 48, OPicture = 52, OSceneNits = 56, OAmount = 60, OCeiling = 64, OKneeEff = 68,
            OOutputPQ = 72, OSrcMax = 76, OClipFrac = 80, OBottomBar = 84, OColorProtect = 88, OFullPct = 92, OTextSkip = 96, OHotSpot = 108, OFixShape = 120, OGrid = 128, OLookAhead = 132,
            OLogRequest = 136, OLogDone = 140, OLogState = 144, OLogStateTick = 148, OHotBelow = 152, OGradedSpot = 156, OBuildKind = 160, OTestMarker = 164, OSpotMask = 168, OInfo = 172, OGpuLoad = 176, ODimFire = 180, OUpscaler = 184, OEdition = 188, OPlaySource = 192, ODither = 196, OProjector = 200, OLutOff = 204, OLut = 208, OSource = 212, OSsr = 216, OFel = 220, OOutGamma = 224, ODoviEl = 228, OOutRange = 232, ODepthDetail = 236, ODepthEdges = 240, ODepthWide = 244, ODepthView = 248;

        readonly MemoryMappedFile map;
        readonly MemoryMappedViewAccessor view;

        public Shared()
        {
            map = MemoryMappedFile.CreateOrOpen(@"Local\ClipGuard_v1", 256);
            view = map.CreateViewAccessor(0, 256);
        }

        public void Publish(Settings s)
        {
            view.Write(OEnabled, s.Enabled ? 1 : 0);
            view.Write(OStrength, (float)s.Strength);
            view.Write(OTrigger, (float)s.TriggerPct);
            view.Write(OKnee, (float)s.Knee);
            view.Write(OView, s.View);
            view.Write(OColorProtect, (float)s.ColorProtect);
            view.Write(OFullPct, (float)s.FullPct);
            view.Write(OTextSkip, (float)s.TextSkipPct);
            view.Write(OHotSpot, (float)s.HotSpot);
            view.Write(OHotBelow, (float)s.HotBelow);
            view.Write(OFixShape, s.FixShape);
            view.Write(OGradedSpot, (float)s.GradedSpot);
            view.Write(OSpotMask, s.SpotMask);
            view.Write(ODimFire, s.DimFire ? 1 : 0);
            view.Write(OUpscaler, Edition.Plus ? s.Upscaler : 0);
            view.Write(OSsr, Edition.Plus ? s.Ssr : 0);   // 2.85.0: SSimSuperRes
            view.Write(OOutGamma, s.OutGamma);           // 2.88.1: 0 = 2.2, 1 = 2.4, 2 = BT.1886
            // 2.94.0: one Depth level sets the renderer's three layers: Detail and Edges at it, the wide one at half
            view.Write(ODepthDetail, s.DepthLevel);
            view.Write(ODepthEdges, s.DepthLevel);
            view.Write(ODepthWide, s.DepthLevel / 2);
            view.Write(ODepthView, 0);
            view.Write(OEdition, Edition.Plus ? 1 : 0);
            // 2.64.1 (Player): 2 = Video Processor, 3 = Media Player.
            view.Write(OPlaySource, s.PlayerSource == 2 ? 2 : (s.PlayerSource == 1 ? 3 : 0));
            view.Write(ODither, s.Dither);
            view.Write(OProjector, s.Projector);
            view.Write(OLutOff, s.LutOff ? 1 : 0);
            int lut = s.Lut;
#if PLAYER
            if (ControlForm.PlayerMode) lut = 0;   // 2.66.0: the player uses its Calibration LUT (Picture), not ClipGuard's
#endif
            view.Write(OLut, lut);
            view.Write(OSource, s.Source);
            view.Write(OInfo, s.Info ? 1 : 0);
            view.Write(OGrid, s.Grid);
            view.Write(OLookAhead, s.EffectiveLookAhead());
            view.Write(OOverlayOn, s.OverlayOn ? 1 : 0);
            view.Write(OPinned, s.Pinned ? 1 : 0);
            view.Write(OSelected, s.Selected);
            view.Write(OLastKey, s.LastKeyTick);
            view.Write(OBuildKind, 0);
            view.Write(OVersion, (uint)1);
            view.Write(OMagic, Magic);
            Heartbeat();
        }

        public void Heartbeat() { view.Write(OExeTick, (uint)Environment.TickCount); }
        // Info banner: the graphics card's load in % + 1 (0 = not known).
        public void SetGpuLoad(float v) { view.Write(OGpuLoad, v); }
        public void SetOutRange(int v) { view.Write(OOutRange, v); }   // 2.92.0: 0 not known, 1 RGB Full, 2 RGB Limited

        // Log (F11): 0 none, 1 saving, 4 failed, 6 saved.
        public void RequestLog(uint id) { view.Write(OLogRequest, id); }
        public uint LogDone { get { return view.ReadUInt32(OLogDone); } }
        // Hot spot test video: (count << 8) | part, written by the renderer when it sees an end-of-part code.
        public uint TestMarker { get { return view.ReadUInt32(OTestMarker); } }
        public void SetLogState(int state)
        {
            view.Write(OLogStateTick, (uint)Environment.TickCount);
            view.Write(OLogState, state);
        }
        public void Retire() { view.Write(OExeTick, (uint)0); }

        public bool RendererAlive
        {
            get { return unchecked((uint)Environment.TickCount - view.ReadUInt32(ORendTick)) < 3000u; }
        }
        public float Picture { get { return view.ReadSingle(OPicture); } }
        public float SceneNits { get { return view.ReadSingle(OSceneNits); } }
        public float Amount { get { return view.ReadSingle(OAmount); } }
        public float Ceiling { get { return view.ReadSingle(OCeiling); } }
        public bool OutputPQ { get { return view.ReadInt32(OOutputPQ) != 0; } }
        public int DoviEl { get { return view.ReadInt32(ODoviEl); } }   // 2.89.0: written by the renderer, 1 MEL, 2 FEL

        public void Dispose()
        {
            view.Dispose();
            map.Dispose();
        }
    }

#if TEST
    // 2.63.0: Learn remote (Test build). The player's Settings > Learn remote asks for one
    // button per action and stores what it sends in clipguard.ini [Remote], next to
    // ClipGuard.exe: "K:<keys>" for a key (with Ctrl/Shift/Alt), "A:<n>" for a WM_APPCOMMAND,
    // empty when skipped. The player turns a learned button into the key it already
    // understands; ClipGuard.exe uses Menu (its F7 menu) and, while that menu shows, the
    // learned arrows, OK and Back. The keyboard keys keep working alongside.
    static class Remote
    {
        public static readonly string[] Actions = { "Up", "Down", "Left", "Right", "OK", "Back", "PlayPause", "Stop",
            "NextChapter", "PrevChapter", "SkipForward", "SkipBack", "Menu", "VideoProcessor", "MediaPlayer", "Banner"
            };
        public static readonly string[] Names = { "Up", "Down", "Left", "Right", "OK (select)", "Back",
            "Play / Pause", "Stop", "Next chapter", "Previous chapter", "Skip forward", "Skip back", "Menu (track menu; ClipGuard menu)",
            "Video Processor (the capture picture)", "Media Player (back to the library)", "Banner (show or hide the film's bar)" };
        // What each action becomes: the keys the player and its library already know.
        // 2.74.0: Video Processor = Ctrl+V, Media Player = Ctrl+M (also keyboard shortcuts).
        public static readonly Keys[] Canon = { Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Enter, Keys.BrowserBack,
            Keys.MediaPlayPause, Keys.MediaStop, Keys.MediaNextTrack, Keys.MediaPreviousTrack,
            Keys.Right | Keys.Shift, Keys.Left | Keys.Shift, Keys.Apps, Keys.V | Keys.Control, Keys.M | Keys.Control, Keys.I   // 2.80.0: Banner = I
            };
        public const string Section = "Remote";

        public static string[] Load(string ini)
        {
            string[] v = new string[Actions.Length];
            for (int i = 0; i < v.Length; i++) v[i] = Native.ReadIni(ini, Section, Actions[i], "").Trim();
            return v;
        }

        public static void Save(string ini, string[] v)
        {
            for (int i = 0; i < Actions.Length; i++)
                try { Native.WritePrivateProfileString(Section, Actions[i], v[i] ?? "", ini); } catch (Exception) { }
        }

        public static string OfKey(Keys k) { return "K:" + ((int)k).ToString(CultureInfo.InvariantCulture); }
        public static string OfApp(int cmd) { return "A:" + cmd.ToString(CultureInfo.InvariantCulture); }

        public static int Find(string[] map, string code)
        {
            if (map == null || string.IsNullOrEmpty(code)) return -1;
            for (int i = 0; i < map.Length; i++) if (map[i] == code) return i;
            return -1;
        }

        // A learned key becomes its action's key; anything else is left as it is.
        public static Keys Translate(string[] map, Keys k)
        {
            int i = Find(map, OfKey(k));
            return i >= 0 ? Canon[i] : k;
        }

        // A learned WM_APPCOMMAND becomes its action's key; Keys.None when not learned.
        public static Keys TranslateApp(string[] map, int cmd)
        {
            int i = Find(map, OfApp(cmd));
            return i >= 0 ? Canon[i] : Keys.None;
        }

        // The learned key for an action (Keys.None when skipped or learned as a WM_APPCOMMAND).
        public static Keys KeyOf(string[] map, string action)
        {
            int i = Array.IndexOf(Actions, action);
            if (map == null || i < 0 || i >= map.Length || map[i] == null || !map[i].StartsWith("K:", StringComparison.Ordinal)) return Keys.None;
            int n;
            return int.TryParse(map[i].Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? (Keys)n : Keys.None;
        }

        public static string Describe(string code)
        {
            if (string.IsNullOrEmpty(code)) return "(skipped)";
            int n;
            if (!int.TryParse(code.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return code;
            if (code.StartsWith("A:", StringComparison.Ordinal)) return "app command " + n.ToString(CultureInfo.InvariantCulture);
            Keys k = (Keys)n;
            string mods = ((k & Keys.Control) != 0 ? "Ctrl+" : "") + ((k & Keys.Alt) != 0 ? "Alt+" : "") + ((k & Keys.Shift) != 0 ? "Shift+" : "");
            return mods + (k & Keys.KeyCode).ToString();
        }
    }
#endif

    class Settings
    {
        public bool Enabled = true;
        public double Strength = 0.35;
        public double TriggerPct = 10.0; // "Fix starts below"
        public double FullPct = 3.0;     // "Full effect below"
        public double Knee = 0.40;
        public double ColorProtect = 0.90;
        public double TextSkipPct = 25.0; // no fix when Text reads above this (0 = off)
        public double HotSpot = 0.6;      // local exposure cut on small, very bright spots (0 = off)
        public double HotSpotLast = 0.6;  // value F10 turns Hot spot back on to
        public double HotBelow = 3.0;     // hot spot only in pictures darker than this % (0 = off)
        public int FixShape = 1;          // 0 = Normal, 1 = Anchor
        public bool Info;                 // info banner along the top of the picture (F6); off at each start
        public int SpotMask = 1;          // Hot spot mask: 0 = Patch, 1 = Pixel (only the light, feathered)
        public bool DimFire;              // Dim fire: false = clearly orange or red lights (flames, candles) are left alone
        public int Upscaler = 0;          // Upscaler: 0 = Normal, 1 = FSRCNNX, 2 = ArtCNN C4F32 (2.85.0) (neural 2x for 1080p on a 4K screen)
        public int Ssr = 0;               // 2.85.0: SSimSuperRes after scaling (1 = on)
        public int OutGamma = 0;          // 2.88.1: the projector's gamma: 0 = 2.2 (Unplugged's), 1 = 2.4, 2 = BT.1886
        public int DepthLevel = 0;        // 2.94.0: Depth 0-100 (0 = off, micro contrast back); 2.95.0: named ClipBee, off by default (25 recommended)
        public int Dither = 0;            // Dither: 0 = Static, 1 = Temporal (the pattern moves every frame) (2.45.0)
        public bool LutOff = false;       // 2.48.2: F5, calibration LUT off for comparing (not saved: always starts on)
        public int Projector = 0;         // Projector: 0 = Universal (Reactive), 1 = Titan Noir Max DBLE High (Predictive) (2.46.0), 3 = DBLE Low (2.60.0); Valerion (2) removed in 2.51.2
        public int Lut = 0;               // 2.50.0: LUT: 0 = None, 1 = Titan (titan.cube), 2 = Valerion (valerion.cube)
        public int Source = 0;            // 2.52.0 (Test): Source: 0 = Video Processor, 1 = Media Player (not saved: starts on Video Processor)
        public double GradedSpot = 0.5;   // gentle local highlight dimming of small graded highlights (0 = off)
        public int Grid = 1;              // measuring grid: 0 normal, 1 fine
        public int LookAhead = 4;         // frames the picture is held back: 0, 2, 4, 6, 12 = High Performance (ClipGuard+)
        public int View = 0;
        public bool Pinned = false;
        public int VpLookAhead = 4;       // 2.64.1 (Player): Look-ahead for Video Processor (capture); LookAhead is the Media Player's
        // not saved
        public int PlayerSource = 0;      // 2.64.1 (Player): 0 none, 1 Media Player (a film), 2 Video Processor (capture)
        public bool OverlayOn = false;
        public int Selected = 0;
        public uint LastKeyTick = 0;

        public static readonly string[] ViewNames = { "Normal", "Guard areas", "Split screen", "Side by side" };
        // Menu rows: ClipGuard (not +) has no Upscaler row. The last row is "Restart & update".
        // 2.63.1: the Test-only Source row is gone (the ClipGuard+ Player opens the Media Player).
        public const int RowUpscaler = 15;
        public static int Rows { get { return 22 - (Edition.Plus ? 0 : 1); } }
        public static int RowOf(int shown)
        {
            if (!Edition.Plus && shown >= RowUpscaler) shown++;
            return shown;
        }

        // What the renderer is told: Video Processor's look-ahead while it captures.
        public int EffectiveLookAhead()
        {
            int v = PlayerSource == 2 ? NormLookAhead(VpLookAhead) : LookAhead;
            return Edition.Plus ? v : Math.Min(v, 6);
        }

        public static Settings Load(string ini)
        {
            Settings s = new Settings();
            s.Enabled = true; // always starts on; F8 turns it off for this session
            s.Strength = Clamp(Read(ini, "Strength", 0.35), 0.0, 1.0);
            s.TriggerPct = Clamp(Read(ini, "FixStartPct", 10.0), 1.0, 50.0);
            s.FullPct = Clamp(Read(ini, "FullEffectPct", 3.0), 0.5, s.TriggerPct - 0.5);
            s.Knee = Clamp(Read(ini, "HighlightStart", 0.40), 0.20, 0.95);
            s.ColorProtect = Clamp(Read(ini, "ColorProtect", 0.90), 0.0, 1.0);
            s.TextSkipPct = Clamp(Read(ini, "TextSkipPct", 25.0), 0.0, 100.0);
            s.HotSpot = Clamp(Read(ini, "HotSpot", 0.6), 0.0, 1.0);
            s.HotBelow = Clamp(Read(ini, "HotSpotBelowPct", 3.0), 0.0, 20.0);
            s.FixShape = (int)Clamp(Read(ini, "FixShape", 1), 0, 1);
            s.GradedSpot = Clamp(Read(ini, "GradedSpot", 0.5), 0.0, 1.0);
            s.SpotMask = (int)Clamp(Math.Round(Read(ini, "HotSpotMask", 1)), 0, 1);
            s.DimFire = Read(ini, "DimFire", 0) > 0.5;
            s.Upscaler = Edition.Plus ? (int)Clamp(Math.Round(Read(ini, "Upscaler", 0)), 0, 2) : 0;
            s.Ssr = Edition.Plus ? (int)Clamp(Math.Round(Read(ini, "SSimSuperRes", 0)), 0, 1) : 0;
            s.OutGamma = (int)Clamp(Math.Round(Read(ini, "OutputGamma", 0)), 0, 2);
            s.DepthLevel = (int)Clamp(Math.Round(Read(ini, "ClipBee", 0)), 0, 100);   // 2.95.0 (was "Depth")
            s.HotSpotLast = Clamp(Read(ini, "HotSpotLast", s.HotSpot > 0.0 ? s.HotSpot : 0.6), 0.05, 1.0);
            s.Grid = (int)Clamp(Read(ini, "Grid", 1), 0, 1);
            s.LookAhead = NormLookAhead((int)Math.Round(Read(ini, "LookAhead", 4)));
            if (!Edition.Plus && s.LookAhead > 6) s.LookAhead = 6;   // High Performance is ClipGuard+ only
            s.Dither = (int)Clamp(Math.Round(Read(ini, "Dither", 0)), 0, 1);
            int pj = (int)Clamp(Math.Round(Read(ini, "Projector", 0)), 0, 3);
            // 2.51.2: 0 = Universal (Reactive), 1 = Titan Noir Max DBLE High (Predictive); Valerion (2) becomes Universal with the Valerion LUT.
            // 2.60.0: 3 = Titan Noir Max DBLE Low (Predictive).
            s.Projector = (pj == 1 || pj == 3) ? pj : 0;
            s.Lut = (int)Clamp(Math.Round(Read(ini, "Lut", pj == 3 ? 1 : pj)), 0, 2);
            // 2.64.1: Video Processor's look-ahead (4 frames).
            s.VpLookAhead = NormLookAhead((int)Math.Round(Read(ini, "VideoProcessorLookAhead", 4)));
            s.View = (int)Clamp(Read(ini, "View", 0), 0, 3);
            s.Pinned = Read(ini, "OverlayPinned", 0) > 0.5;
            return s;
        }

        public void Save(string ini)
        {
            W(ini, "Enabled", Enabled ? "1" : "0");
            W(ini, "Strength", Strength.ToString("0.00", CultureInfo.InvariantCulture));
            W(ini, "FixStartPct", TriggerPct.ToString("0.0", CultureInfo.InvariantCulture));
            W(ini, "FullEffectPct", FullPct.ToString("0.0", CultureInfo.InvariantCulture));
            W(ini, "HighlightStart", Knee.ToString("0.00", CultureInfo.InvariantCulture));
            W(ini, "ColorProtect", ColorProtect.ToString("0.00", CultureInfo.InvariantCulture));
            W(ini, "TextSkipPct", TextSkipPct.ToString("0", CultureInfo.InvariantCulture));
            W(ini, "HotSpot", HotSpot.ToString("0.00", CultureInfo.InvariantCulture));
            W(ini, "HotSpotLast", HotSpotLast.ToString("0.00", CultureInfo.InvariantCulture));
            W(ini, "HotSpotBelowPct", HotBelow.ToString("0.0", CultureInfo.InvariantCulture));
            W(ini, "FixShape", FixShape.ToString(CultureInfo.InvariantCulture));
            W(ini, "GradedSpot", GradedSpot.ToString("0.00", CultureInfo.InvariantCulture));
            W(ini, "HotSpotMask", SpotMask.ToString(CultureInfo.InvariantCulture));
            W(ini, "DimFire", DimFire ? "1" : "0");
            W(ini, "OutputGamma", OutGamma.ToString(CultureInfo.InvariantCulture));
            W(ini, "ClipBee", DepthLevel.ToString(CultureInfo.InvariantCulture));   // 2.95.0
            if (Edition.Plus)
            {
                W(ini, "Upscaler", Upscaler.ToString(CultureInfo.InvariantCulture));
                W(ini, "SSimSuperRes", Ssr.ToString(CultureInfo.InvariantCulture));
            }
            W(ini, "Dither", Dither.ToString(CultureInfo.InvariantCulture));
            W(ini, "Projector", Projector.ToString(CultureInfo.InvariantCulture));
            W(ini, "Lut", Lut.ToString(CultureInfo.InvariantCulture));
            W(ini, "Grid", Grid.ToString(CultureInfo.InvariantCulture));
            W(ini, "LookAhead", LookAhead.ToString(CultureInfo.InvariantCulture));
            W(ini, "VideoProcessorLookAhead", VpLookAhead.ToString(CultureInfo.InvariantCulture));
            W(ini, "View", View.ToString(CultureInfo.InvariantCulture));
            W(ini, "OverlayPinned", Pinned ? "1" : "0");
        }

        static void W(string ini, string key, string value)
        {
            Native.WritePrivateProfileString("ClipGuard", key, value, ini);
        }

        static double Read(string ini, string key, double def)
        {
            string s = Native.ReadIni(ini, "ClipGuard", key, "").Trim();
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        // 0, 2, 4 or 6 frames (1 = "on" from 2.12.0 = 2).
        public static int NormLookAhead(int v) { return v <= 0 ? 0 : (v <= 2 ? 2 : (v <= 4 ? 4 : (v <= 6 ? 6 : 12))); }   // 12 = High Performance

        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ---- the shortcuts (no window) ---------------------------------------------
    // A hidden form: it only exists to receive the hotkeys. The on-screen
    // overlay, drawn by the renderer, is the user interface.

    class ControlForm : Form
#if TEST
        , MediaPlayer.IHost
#endif
#if PLAYER
        , MediaPlayer.IPlayerHost
#endif
    {
        public static readonly uint ShowMeMessage = Native.RegisterWindowMessage("ClipGuard_ShowWindow");

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
#if TEST
        // 2.63.0: learned remote buttons (Settings > Learn remote in the player): Menu opens
        // this menu; while it shows, the learned arrows, OK (= Right) and Back (closes it).
        const int HK_RMENU = 30, HK_RUP = 31, HK_RDOWN = 32, HK_RLEFT = 33, HK_RRIGHT = 34, HK_ROK = 35, HK_RBACK = 36;
        string[] remote;
        static readonly string[] RemoteNav = { "Up", "Down", "Left", "Right", "OK", "Back" };

        bool RegisterRemote(int id, string action)
        {
            Keys k = Remote.KeyOf(remote, action);
            Keys kc = k & Keys.KeyCode;
            if (kc == Keys.None) return false;
            uint mods = ((k & Keys.Control) != 0 ? MOD_CONTROL : 0) | ((k & Keys.Alt) != 0 ? MOD_ALT : 0) | ((k & Keys.Shift) != 0 ? MOD_SHIFT : 0);
            // The plain arrows are taken already while the menu shows; F-keys keep their jobs.
            if (mods == 0 && (kc == Keys.Up || kc == Keys.Down || kc == Keys.Left || kc == Keys.Right || (kc >= Keys.F5 && kc <= Keys.F11))) return false;
            return Native.RegisterHotKey(Handle, id, mods | (id == HK_RMENU || id == HK_ROK || id == HK_RBACK ? MOD_NOREPEAT : 0), (uint)kc);
        }
#endif
        const int HK_O = 1, HK_D = 2, HK_UP = 3, HK_DOWN = 4, HK_LEFT = 5, HK_RIGHT = 6;
        const int HK_F7 = 7, HK_F8 = 8;
        // Plain arrow keys: registered only while the overlay is showing, so
        // Unplugged keeps them (seek, menus) the rest of the time.
        const int HK_PUP = 9, HK_PDOWN = 10, HK_PLEFT = 11, HK_PRIGHT = 12;
        const int HK_F9 = 13; // Fix areas view on/off, without the overlay
        const int HK_F10 = 14; // Hot spot off / back to its last value
        const int HK_F11 = 15; // save and send a log
        const int HK_U = 16;   // Ctrl+Alt+U: restart and update
        const int HK_F6 = 17;  // info banner on/off
        const int HK_F5 = 18;  // 2.48.2: calibration LUT on/off (comparison; starts on)
        const uint OverlayHideMs = 8000;

        readonly string iniPath;
        readonly string seedIni;
        readonly Settings s;
        readonly Shared shared;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        DateTime startTime = DateTime.Now;
        DateTime saveDue = DateTime.MaxValue;
        bool plainArrows;
        // The shortcuts are taken only while Unplugged is the window in front;
        // every other program keeps F7-F11 and the arrow keys.
        readonly System.Windows.Forms.Timer focusTimer = new System.Windows.Forms.Timer();
        bool keysOn;
        IntPtr lastFront = IntPtr.Zero;
        bool lastFrontIsPlayer;
        bool restarting;
        // The info banner hides itself kInfoSeconds after F6 showed it.
        const int kInfoSeconds = 8;
        DateTime infoDue = DateTime.MaxValue;
        // log (F11)
        uint logId;
        string logStamp;
        DateTime logStart;
        bool logBusy;

        public ControlForm(string iniPath, string seedIni)
        {
            this.iniPath = iniPath;
            this.seedIni = seedIni;
            s = Settings.Load(iniPath);
#if TEST
            remote = Remote.Load(seedIni);
#endif
            shared = new Shared();
            shared.Publish(s);

            Text = "ClipGuard";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;

            timer.Interval = 500;
            timer.Tick += delegate { OnTick(); };
            timer.Start();
            focusTimer.Interval = 150;
            focusTimer.Tick += delegate { UpdateKeys(); InfoTimeout(); };
            focusTimer.Start();
            gpuTimer = new System.Threading.Timer(GpuSample, null, 500, 500);
        }

        // Info banner "GPU load": the card's load as Windows counts it (the 3D
        // figure in Task Manager), read twice a second while the banner shows.
        // With more than one card, the busiest one.
        System.Threading.Timer gpuTimer;
        Dictionary<string, CounterSample> gpuPrev = new Dictionary<string, CounterSample>();
        int gpuBusy, rangeN;
        void GpuSample(object unused)
        {
            if (Interlocked.Exchange(ref gpuBusy, 1) != 0) return;
            try
            {
                if (!s.Info)
                {
                    if (gpuPrev.Count > 0) { gpuPrev.Clear(); shared.SetGpuLoad(0f); }
                    return;
                }
                InstanceDataCollectionCollection all = new PerformanceCounterCategory("GPU Engine").ReadCategory();
                InstanceDataCollection col = all["Utilization Percentage"];
                if (col == null) return;
                var cur = new Dictionary<string, CounterSample>();
                var sums = new Dictionary<string, double>();
                foreach (InstanceData d in col.Values)
                {
                    string n = d.InstanceName;
                    if (n.IndexOf("engtype_3d", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    cur[n] = d.Sample;
                    CounterSample p;
                    if (!gpuPrev.TryGetValue(n, out p)) continue;
                    double v = CounterSample.Calculate(p, d.Sample);
                    if (double.IsNaN(v) || v < 0.0) continue;
                    int a = n.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                    int b = a >= 0 ? n.IndexOf("_phys", a, StringComparison.OrdinalIgnoreCase) : -1;
                    string card = (a >= 0 && b > a) ? n.Substring(a, b - a) : "";
                    double t;
                    sums.TryGetValue(card, out t);
                    sums[card] = t + v;
                }
                // 2.92.0: the HDMI range the NVIDIA driver sends, every 2 s while the banner shows
                if (++rangeN % 4 == 1) { try { shared.SetOutRange(NvRange.Read()); } catch { } }
                bool had = gpuPrev.Count > 0;
                gpuPrev = cur;
                if (!had) return;   // the first reading has nothing to compare with
                double top = 0.0;
                foreach (double v in sums.Values) if (v > top) top = v;
                shared.SetGpuLoad((float)(Math.Min(top, 100.0) + 1.0));
            }
            catch { }   // no such counters on this Windows: the banner leaves the line out
            finally { Interlocked.Exchange(ref gpuBusy, 0); }
        }

        void InfoTimeout()
        {
            if (!s.Info || DateTime.Now < infoDue) return;
            s.Info = false;
            infoDue = DateTime.MaxValue;
            shared.Publish(s);
        }

        // Never shown: create the handle (for the hotkeys) and stay hidden.
        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateKeys();
        }

#if PLAYER
        // 2.54.0: ClipGuard+ Player (test): ClipGuard runs inside the player. Its keys
        // work while the film's picture is in front, the way they do with Unplugged.
        public static bool PlayerMode;
        public static IntPtr PlayerVideoWindow = IntPtr.Zero;
#endif
        bool PlayerInFront()
        {
            IntPtr w = Native.GetForegroundWindow();
            if (w == IntPtr.Zero) return false;
#if PLAYER
            if (PlayerMode) return w == PlayerVideoWindow && Native.IsWindowVisible(w);
#endif
            if (w == lastFront) return lastFrontIsPlayer;
            bool player = false;
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(w, out pid);
                if (pid != 0)
                {
                    using (Process p = Process.GetProcessById((int)pid))
                        player = string.Equals(p.ProcessName, "Unplugged", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { }
            lastFront = w;
            lastFrontIsPlayer = player;
            return player;
        }

        // Take the shortcuts when Unplugged comes to the front, give them back
        // when another program does.
        void UpdateKeys()
        {
            if (!IsHandleCreated) return;
            bool want = PlayerInFront();
            if (want != keysOn)
            {
                if (want)
                {
                    Native.RegisterHotKey(Handle, HK_O, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.O);
                    Native.RegisterHotKey(Handle, HK_D, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.D);
                    Native.RegisterHotKey(Handle, HK_UP, MOD_CONTROL | MOD_ALT, (uint)Keys.Up);
                    Native.RegisterHotKey(Handle, HK_DOWN, MOD_CONTROL | MOD_ALT, (uint)Keys.Down);
                    Native.RegisterHotKey(Handle, HK_LEFT, MOD_CONTROL | MOD_ALT, (uint)Keys.Left);
                    Native.RegisterHotKey(Handle, HK_RIGHT, MOD_CONTROL | MOD_ALT, (uint)Keys.Right);
                    Native.RegisterHotKey(Handle, HK_F7, MOD_NOREPEAT, (uint)Keys.F7);
                    Native.RegisterHotKey(Handle, HK_F8, MOD_NOREPEAT, (uint)Keys.F8);
                    Native.RegisterHotKey(Handle, HK_F9, MOD_NOREPEAT, (uint)Keys.F9);
                    Native.RegisterHotKey(Handle, HK_F10, MOD_NOREPEAT, (uint)Keys.F10);
                    Native.RegisterHotKey(Handle, HK_F11, MOD_NOREPEAT, (uint)Keys.F11);
                    Native.RegisterHotKey(Handle, HK_U, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.U);
                    Native.RegisterHotKey(Handle, HK_F6, MOD_NOREPEAT, (uint)Keys.F6);
                    Native.RegisterHotKey(Handle, HK_F5, MOD_NOREPEAT, (uint)Keys.F5);
#if TEST && !PLAYER
                    // In the player the Menu button opens its track menu (F7 still opens this one).
                    RegisterRemote(HK_RMENU, "Menu");
#endif
                }
                else
                {
                    Native.UnregisterHotKey(Handle, HK_O);
                    Native.UnregisterHotKey(Handle, HK_D);
                    Native.UnregisterHotKey(Handle, HK_F6);
                    Native.UnregisterHotKey(Handle, HK_F5);
                    for (int id = HK_UP; id <= HK_F8; id++) Native.UnregisterHotKey(Handle, id);
                    for (int id = HK_F9; id <= HK_U; id++) Native.UnregisterHotKey(Handle, id);
#if TEST
                    Native.UnregisterHotKey(Handle, HK_RMENU);
#endif
                }
                keysOn = want;
            }
            UpdatePlainArrows();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                OnHotKey(m.WParam.ToInt32());
                return;
            }
            if (ShowMeMessage != 0 && (uint)m.Msg == ShowMeMessage)
            {
                // ClipGuard.exe was started again: show the overlay.
                s.OverlayOn = true;
                s.LastKeyTick = (uint)Environment.TickCount;
                Changed();
                UpdatePlainArrows();
                return;
            }
            base.WndProc(ref m);
        }

        bool OverlayVisible()
        {
            if (!s.OverlayOn) return false;
            if (s.Pinned) return true;
            return unchecked((uint)Environment.TickCount - s.LastKeyTick) < OverlayHideMs;
        }

        void OnHotKey(int id)
        {
            bool visible = OverlayVisible();
            uint now = (uint)Environment.TickCount;
            if (id == HK_F11) { StartLog(); return; }
            if (id == HK_U) { RestartAndUpdate(); return; }
#if TEST
            // Learned remote buttons act as the keys they stand for.
            if (id == HK_RMENU) id = HK_F7;
            else if (id == HK_RUP) id = HK_PUP;
            else if (id == HK_RDOWN) id = HK_PDOWN;
            else if (id == HK_RLEFT) id = HK_PLEFT;
            else if (id == HK_RRIGHT || id == HK_ROK) id = HK_PRIGHT;
            else if (id == HK_RBACK) { s.OverlayOn = false; Changed(); UpdatePlainArrows(); return; }
#endif
            switch (id)
            {
                case HK_F7:
                case HK_O:
                    if (visible) s.OverlayOn = false;
                    else { s.OverlayOn = true; s.LastKeyTick = now; }
                    break;
                case HK_F6:
                    // Info banner on/off; the overlay stays as it is.
                    s.Info = !s.Info;
                    infoDue = DateTime.MaxValue;   // 2.84.0: stays up until F6 / Home again
                    break;
                case HK_F9:
                    // Fix areas on/off; the overlay stays as it is.
                    s.View = (s.View == 1) ? 0 : 1;
                    break;
                case HK_F10:
                    // Hot spot off, or back on at the value it had.
                    if (s.HotSpot > 0.0) { s.HotSpotLast = s.HotSpot; s.HotSpot = 0.0; }
                    else s.HotSpot = s.HotSpotLast > 0.0 ? s.HotSpotLast : 0.6;
                    break;   // silent: the overlay stays as it is
                case HK_F5:
#if PLAYER
                    if (PlayerMode) { if (media != null) media.ToggleCubeLut(); break; }   // 2.66.0: the player's Calibration LUT
#endif
                    s.LutOff = !s.LutOff;
                    break;   // silent: the banner shows it
                case HK_F8:
                case HK_D:
                    s.Enabled = !s.Enabled;
                    break;   // silent: the overlay stays as it is
                case HK_UP:
                case HK_DOWN:
                case HK_PUP:
                case HK_PDOWN:
                    if (visible) s.Selected = (s.Selected + ((id == HK_UP || id == HK_PUP) ? Settings.Rows - 1 : 1)) % Settings.Rows;
                    s.OverlayOn = true;
                    s.LastKeyTick = now;
                    break;
                case HK_LEFT:
                case HK_RIGHT:
                case HK_PLEFT:
                case HK_PRIGHT:
                    if (visible && s.Selected == Settings.Rows - 1)
                    {
                        if (id == HK_RIGHT || id == HK_PRIGHT) { RestartAndUpdate(); return; }
                        break;
                    }
                    if (visible) Step((id == HK_RIGHT || id == HK_PRIGHT) ? 1 : -1);
                    s.OverlayOn = true;
                    s.LastKeyTick = now;
                    break;
            }
            Changed();
            UpdatePlainArrows();
#if PLAYER
            if (media != null) media.PausedRedraw((int)OverlayHideMs + 1500);   // 2.102.0: a paused film shows the change
#endif
        }

        // Close Unplugged and start ClipGuard again: the new start (Test build)
        // installs a newer build if there is one, then starts Unplugged.
        void RestartAndUpdate()
        {
#if PLAYER
            if (PlayerMode) return;   // the player updates with ClipGuard (start ClipGuard to update)
#endif
            if (restarting) return;
            restarting = true;
            try { s.Save(iniPath); } catch (Exception) { }
            Process[] procs = Process.GetProcessesByName("Unplugged");
            foreach (Process p in procs)
            {
                try { p.CloseMainWindow(); } catch (Exception) { }
            }
            DateTime until = DateTime.Now.AddSeconds(10);
            foreach (Process p in procs)
            {
                try
                {
                    int left = (int)Math.Max(0, (until - DateTime.Now).TotalMilliseconds);
                    if (!p.WaitForExit(left)) { p.Kill(); p.WaitForExit(5000); }
                }
                catch (Exception) { }
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                    "/restart " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception) { }
            Close();
        }

        // F11: the renderer saves the log; OnTick sends it once it is there.
        void StartLog() { StartLog(""); }

        void StartLog(string suffix)
        {
            if (logBusy) return;
            logId = (uint)Math.Max(1.0, (DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            logStamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + suffix;
            logStart = DateTime.Now;
            logBusy = true;
            shared.SetLogState(1);
            shared.RequestLog(logId);
        }

        void PollLog()
        {
            if (!logBusy) return;
            if (shared.LogDone == logId)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "logs");
                string stem = Path.Combine(dir, "log_" + logId.ToString(CultureInfo.InvariantCulture));
                string stamp = logStamp;
                // Public: keep it as <date>.csv and <date>.png, and open the folder.
                bool ok = SaveLog(dir, stem, stamp, false);
                shared.SetLogState(ok ? 6 : 4);
                logBusy = false;
                if (ok)
                {
                    try { Process.Start("explorer.exe", "/select,\"" + Path.Combine(dir, stamp + ".png") + "\""); } catch (Exception) { }
                }
            }
            else if ((DateTime.Now - logStart).TotalSeconds > 20)
            {
                shared.SetLogState(4); // the renderer did not save it (nothing playing?)
                logBusy = false;
            }
        }

        // <date>.csv and <date>.png next to the renderer's files; keep: leave those for sending.
        static bool SaveLog(string dir, string stem, string stamp, bool keep)
        {
            try
            {
                if (File.Exists(stem + ".csv")) File.Copy(stem + ".csv", Path.Combine(dir, stamp + ".csv"), true);
                if (File.Exists(stem + ".bmp"))
                {
                    using (Bitmap bmp = new Bitmap(stem + ".bmp"))
                        bmp.Save(Path.Combine(dir, stamp + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    if (!keep) File.Delete(stem + ".bmp");
                }
                if (!keep && File.Exists(stem + ".csv")) File.Delete(stem + ".csv");
                return true;
            }
            catch (Exception) { return false; }
        }

        // Take the plain arrow keys while the overlay shows; give them back after.
        void UpdatePlainArrows()
        {
            bool want = keysOn && OverlayVisible();
            if (want == plainArrows) return;
            if (want)
            {
                Native.RegisterHotKey(Handle, HK_PUP, 0, (uint)Keys.Up);
                Native.RegisterHotKey(Handle, HK_PDOWN, 0, (uint)Keys.Down);
                Native.RegisterHotKey(Handle, HK_PLEFT, 0, (uint)Keys.Left);
                Native.RegisterHotKey(Handle, HK_PRIGHT, 0, (uint)Keys.Right);
#if TEST
                for (int i = 0; i < RemoteNav.Length; i++) RegisterRemote(HK_RUP + i, RemoteNav[i]);
#endif
            }
            else
            {
                for (int id = HK_PUP; id <= HK_PRIGHT; id++) Native.UnregisterHotKey(Handle, id);
#if TEST
                for (int id = HK_RUP; id <= HK_RBACK; id++) Native.UnregisterHotKey(Handle, id);
#endif
            }
            plainArrows = want;
        }

        void Step(int dir)
        {
            switch (Settings.RowOf(s.Selected))
            {
                case 0: s.Enabled = !s.Enabled; break;
                case 1: s.Strength = Settings.Clamp(Math.Round(s.Strength + 0.01 * dir, 2), 0.0, 1.0); break;
                case 2: s.FixShape = 1 - s.FixShape; break;
                case 3:
                    s.TriggerPct = Settings.Clamp(Math.Round(s.TriggerPct + 0.5 * dir, 1), 1.0, 50.0);
                    if (s.FullPct > s.TriggerPct - 0.5) s.FullPct = s.TriggerPct - 0.5; // full stays below start
                    break;
                case 4: s.FullPct = Settings.Clamp(Math.Round(s.FullPct + 0.5 * dir, 1), 0.5, s.TriggerPct - 0.5); break;
                case 5: s.Knee = Settings.Clamp(Math.Round(s.Knee + 0.01 * dir, 2), 0.20, 0.95); break;
                case 6: s.ColorProtect = Settings.Clamp(Math.Round(s.ColorProtect + 0.05 * dir, 2), 0.0, 1.0); break;
                case 7: s.TextSkipPct = Settings.Clamp(Math.Round(s.TextSkipPct + 1.0 * dir), 0.0, 100.0); break;
                case 8:
                    s.HotSpot = Settings.Clamp(Math.Round(s.HotSpot + 0.05 * dir, 2), 0.0, 1.0);
                    if (s.HotSpot > 0.0) s.HotSpotLast = s.HotSpot;
                    break;
                case 9: s.HotBelow = Settings.Clamp(Math.Round(s.HotBelow + 0.1 * dir, 1), 0.0, 20.0); break;
                case 10: s.SpotMask = 1 - s.SpotMask; break;
                case 11: s.GradedSpot = Settings.Clamp(Math.Round(s.GradedSpot + 0.05 * dir, 2), 0.0, 1.0); break;
                case 12: s.DimFire = !s.DimFire; break;
                case 13: s.Grid = 1 - s.Grid; break;
                case 14:
                    if (s.PlayerSource == 2) { s.VpLookAhead = (s.VpLookAhead >= 12) ? (dir > 0 ? 12 : 6) : Settings.NormLookAhead(s.VpLookAhead + 2 * dir); break; }
                    if (Edition.Plus) s.LookAhead = (s.LookAhead >= 12) ? (dir > 0 ? 12 : 6) : Settings.NormLookAhead(s.LookAhead + 2 * dir);   // Off, 2, 4, 6, High Performance
                    else s.LookAhead = Math.Min(6, Settings.NormLookAhead(Math.Min(s.LookAhead, 6) + 2 * dir));   // Off, 2, 4, 6
                    break;
                case 15: if (Edition.Plus) s.Upscaler = (s.Upscaler + (dir < 0 ? 2 : 1)) % 3; break;   // 2.85.0: Normal, FSRCNNX, ArtCNN
                case 16: s.Dither = 1 - s.Dither; break;
                case 17:
                    {
                        // 2.60.0: Universal (Reactive), Titan Noir Max DBLE High, Titan Noir Max DBLE Low (Predictive)
                        int[] order = { 0, 1, 3 };
                        int i = Math.Max(0, Array.IndexOf(order, s.Projector));
                        s.Projector = order[(i + (dir > 0 ? 1 : order.Length - 1)) % order.Length];
                        break;
                    }
                case 18:
#if PLAYER
                    if (PlayerMode) break;   // 2.66.0: the player's LUT is in Picture > Calibration LUT
#endif
                    s.Lut = (s.Lut + (dir > 0 ? 1 : 2)) % 3; break;   // 2.50.0: None, Titan, Valerion
                case 19: s.View = (s.View + (dir > 0 ? 1 : 3)) % 4; break;
                case 20: s.Pinned = !s.Pinned; break;
            }
        }

        // A setting changed: tell the renderer now, save soon.
        void Changed()
        {
            shared.Publish(s);
            saveDue = DateTime.Now.AddSeconds(1);
        }

#if TEST
        uint lastMark;
        bool markInit;
#endif

        void OnTick()
        {
            shared.Heartbeat();
            PollLog();
#if TEST
            // Hot spot test video: at the end of each part, save and send the log
            // as if F11 had been pressed, named ..._test<part>.
            uint mark = shared.TestMarker;
            if (!markInit) { lastMark = mark; markInit = true; }
            else if (mark != lastMark)
            {
                lastMark = mark;
                if ((mark & 0xFF) != 0) StartLog("_test" + (mark & 0xFF).ToString(CultureInfo.InvariantCulture));
            }
#endif
            UpdatePlainArrows();

            if (DateTime.Now >= saveDue)
            {
                saveDue = DateTime.MaxValue;
                try { s.Save(iniPath); } catch (Exception) { }
            }

            // Exit with Unplugged (not while the Media Player has it closed, or is starting it again).
            bool mediaBusy = false;
#if TEST
            mediaBusy = media != null || DateTime.Now < mediaHold;
#endif
#if PLAYER
            if (PlayerMode) return;   // no Unplugged in the player
#endif
            if (!restarting && !mediaBusy && (DateTime.Now - startTime).TotalSeconds > 20 && Process.GetProcessesByName("Unplugged").Length == 0)
                Close();
        }

#if TEST
        // ---- 2.52.0: the Media Player (Plex), Test build only ---------------------------
        MediaPlayer media;
        DateTime mediaHold = DateTime.MinValue;

#if PLAYER
        // The player starts on its Media Player; closing it closes the player.
        public void OpenPlayer(string appDir, string workRoot)
        {
            s.Source = 1;
            s.OverlayOn = false;
            shared.Publish(s);
            media = new MediaPlayer(this, appDir, seedIni, workRoot);
            media.Show();
        }
#endif

        public int LookAheadFrames() { return s.EffectiveLookAhead(); }

#if PLAYER
        // ---- 2.64.1: the player's ClipGuard tab and source (Media Player / Video Processor) ----
        public void SetPlayerSource(int src) { if (s.PlayerSource != src) { s.PlayerSource = src; Changed(); } }
        // 2.83.0: the remote's Home button: the same as F6 (ClipGuard's info banner on / off).
        public void ToggleClipGuardBanner()
        {
            s.Info = !s.Info;
            infoDue = DateTime.MaxValue;   // 2.84.0: stays up until F6 / Home again
            Changed();
            UpdatePlainArrows();
#if PLAYER
            if (media != null) media.PausedRedraw(1500);   // 2.102.0
#endif
        }

        // The tab's rows: the F7 rows (Upscaler is on the Picture tab, View and Menu stay in F7),
        // with the two look-aheads.
        static readonly int[] TabRows = { 0, 17, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, -1, -2, 16 };
        static readonly string[] TabRowNames = { "ClipGuard", "Projector", "Strength", "Fix shape", "Fix starts below", "Full effect below",
            "Highlight start", "Colour protect", "Text skip", "Hot spot", "Hot spot below", "Hot spot mask", "Graded spot", "Dim fire", "Grid",
            "Look-ahead (Media Player)", "Look-ahead (Video Processor)", "Dither pattern" };
        public int TabRowCount() { return TabRows.Length; }
        public string TabRowName(int i) { return TabRowNames[i]; }

        static string LaText(int v) { return v >= 12 ? "High Performance" : (v > 0 ? v.ToString(CultureInfo.InvariantCulture) + " frames" : "Off"); }

        public string TabRowValue(int i)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            switch (TabRows[i])
            {
                case 0: return s.Enabled ? "On" : "Off";
                case 17: return s.Projector == 1 ? "Titan Noir Max DBLE High (Predictive)" : (s.Projector == 3 ? "Titan Noir Max DBLE Low (Predictive)" : "Universal (Reactive)");
                case 18: return s.Lut == 1 ? "Titan" : (s.Lut == 2 ? "Valerion" : "None");
                case 1: return s.Strength.ToString("0.00", c);
                case 2: return s.FixShape == 1 ? "Anchor" : "Normal";
                case 3: return s.TriggerPct.ToString("0.0", c) + " %";
                case 4: return s.FullPct.ToString("0.0", c) + " %";
                case 5: return s.Knee.ToString("0.00", c);
                case 6: return s.ColorProtect.ToString("0.00", c);
                case 7: return s.TextSkipPct > 0 ? s.TextSkipPct.ToString("0", c) + " %" : "Off";
                case 8: return s.HotSpot > 0 ? s.HotSpot.ToString("0.00", c) : "Off";
                case 9: return s.HotBelow > 0 ? s.HotBelow.ToString("0.0", c) + " %" : "Off";
                case 10: return s.SpotMask == 1 ? "Pixel" : "Patch";
                case 11: return s.GradedSpot > 0 ? s.GradedSpot.ToString("0.00", c) : "Off";
                case 12: return s.DimFire ? "Yes" : "No";
                case 13: return s.Grid == 1 ? "Fine" : "Normal";
                case -1: return LaText(s.LookAhead);
                case -2: return LaText(s.VpLookAhead);
                case 16: return s.Dither == 1 ? "Temporal" : "Static";
            }
            return "";
        }

        public void TabRowStep(int i, int dir)
        {
            int r = TabRows[i];
            if (r == -1) s.LookAhead = (s.LookAhead >= 12) ? (dir > 0 ? 12 : 6) : Settings.NormLookAhead(s.LookAhead + 2 * dir);
            else if (r == -2) s.VpLookAhead = (s.VpLookAhead >= 12) ? (dir > 0 ? 12 : 6) : Settings.NormLookAhead(s.VpLookAhead + 2 * dir);
            else
            {
                int keep = s.Selected;
                int shown = r;   // Test / Player builds are ClipGuard+: shown row = row
                s.Selected = shown;
                Step(dir);
                s.Selected = keep;
            }
            Changed();
        }

        // The Picture tab's Upscaler row: FSRCNNX is ClipGuard's.
        public int NeuralUpscaler() { return s.Upscaler; }   // 2.85.0: 0 none, 1 FSRCNNX, 2 ArtCNN C4F32
        public void SetNeuralUpscaler(int v) { v = Math.Max(0, Math.Min(2, v)); if (s.Upscaler != v) { s.Upscaler = v; Changed(); } }
        public bool Ssr() { return s.Ssr == 1; }
        public void SetSsr(bool on) { int v = on ? 1 : 0; if (s.Ssr != v) { s.Ssr = v; Changed(); } }
        public int OutGamma() { return s.OutGamma; }   // 2.88.1
        public void SetOutGamma(int v) { v = Math.Max(0, Math.Min(2, v)); if (s.OutGamma != v) { s.OutGamma = v; Changed(); } }
        // 2.94.0: Depth, one level 0-100
        public int DepthLevel() { return s.DepthLevel; }
        public void SetDepthLevel(int v) { v = Math.Max(0, Math.Min(100, v)); if (s.DepthLevel != v) { s.DepthLevel = v; Changed(); } }
#endif

        public void MediaClosed()
        {
#if PLAYER
            if (PlayerMode) { media = null; Close(); return; }
#endif
            media = null;
            mediaHold = DateTime.Now.AddSeconds(30);   // Unplugged is starting again
            s.Source = 0;
            Changed();
        }
#endif

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
#if TEST
            if (media != null) { try { media.Close(); } catch (Exception) { } }
#endif
            try { s.Save(iniPath); } catch (Exception) { }
            for (int id = HK_O; id <= HK_F6; id++) Native.UnregisterHotKey(Handle, id);
            shared.Retire();
            shared.Dispose();
            base.OnFormClosed(e);
        }
    }

    // 2.84.0 / 2.86.0: a text block for the renderer's top banner: magic "KSC1", tick, chars,
    // UTF-16 text (lines: title, then the column). The renderer ignores it after 5 s without a tick.
    sealed class InfoShared
    {
        readonly string name;
        MemoryMappedFile map;
        MemoryMappedViewAccessor view;
        readonly object gate = new object();

        public InfoShared(string name) { this.name = name; }

        public void Write(string text)
        {
            lock (gate)
            {
                try
                {
                    if (view == null)
                    {
                        map = MemoryMappedFile.CreateOrOpen(name, 4096);
                        view = map.CreateViewAccessor(0, 4096);
                    }
                    if (text == null) text = "";
                    if (text.Length > 2000) text = text.Substring(0, 2000);
                    char[] c = text.ToCharArray();
                    view.Write(8, (uint)c.Length);
                    view.WriteArray(12, c, 0, c.Length);
                    view.Write(4, (uint)Environment.TickCount);
                    view.Write(0, 0x3143534Bu);
                }
                catch (Exception) { }
            }
        }

        // The banner's font has ASCII and Latin-1 punctuation only: accents are dropped.
        public static string Ascii(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Normalize(NormalizationForm.FormD);
            StringBuilder b = new StringBuilder();
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                b.Append(c >= 32 && c < 127 ? c : (c == '\u00B7' ? c : (c == '\u2019' || c == '\u2018' ? '\'' : (c == '\u2013' || c == '\u2014' ? '-' : '?'))));
            }
            return b.ToString();
        }
    }

}
