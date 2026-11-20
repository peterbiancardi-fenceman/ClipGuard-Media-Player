// ClipGuard Media Player (2.52.0, ClipGuard Test package only).
//
// Browses the Plex libraries (all servers of the account, or one) and plays a
// film in Unplugged (with ClipGuard) through the Plex server: direct play of the
// original file, no transcoding (2.52.1; 2.52.0 read it from a network share). Chosen in the F7 menu
// (Source: Media Player). Keyboard: arrows, Enter, Esc / Backspace back, letters
// jump in Movies; F4 steps out (film -> library -> Video Processor).
//
// - Sign-in: a Plex PIN (the short code at plex.tv/link, or the QR code with the
//   long one); the token goes into clipguard.ini next to ClipGuard.exe
//   ([MediaPlayer]) and nowhere else.
// - Playing: Unplugged is closed, switched to Media Player mode (its
//   app_config.json) and started on the server's file (2.52.2: the new Unplugged
//   takes only the file; no resume point, audio delay or position from outside yet). Its position goes back to Plex
//   every 10 s; at 90% or more the film is marked watched. Back to Video
//   Processor: Unplugged's own settings are put back and it starts as usual.
//
// C# 5 (the packages are built with the .NET Framework 4 compiler).

#if TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClipGuard
{
    sealed class Server
    {
        public string Name = "", Id = "", Uri = "", Token = "";
        public bool Owned;
    }

    // 2.55.0: one version of a film (Plex keeps each copy as its own Media).
    // 2.57.0: a sound or subtitle stream as Plex lists it (Index: its place in the file).
    sealed class PStream
    {
        public long Id, Index = -1;
        public string Name = "";
        public bool Selected, External;
    }

    // 2.57.0: a chapter (times in ms).
    sealed class Chap
    {
        public string Title = "", Thumb = "";
        public long Start, End;
    }

    sealed class Ver
    {
        public string Res = "", Hdr = "", Audio = "", PartKey = "", File = "";
        public long Size, PartId;
        public bool Indexes;   // 2.57.0: Plex has preview thumbnails for it
        public List<PStream> Audios = new List<PStream>(), Subs = new List<PStream>();
        public string Label()
        {
            List<string> p = new List<string>();
            string v = (Res + " " + (Hdr == "SDR" ? "" : Hdr)).Trim();
            if (v.Length > 0) p.Add(v);
            if (Audio.Length > 0) p.Add(Audio);
            if (Size > 0) p.Add((Size / 1073741824.0).ToString(Size >= 10737418240L ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB");
            return p.Count > 0 ? string.Join("  \u00b7  ", p.ToArray()) : "Version";
        }
    }

    sealed class Movie
    {
        public Server Srv;
        public List<Ver> Versions = new List<Ver>();
        public int VerIdx;
        public string Key = "", Title = "", Thumb = "", Art = "", Summary = "", Rating = "", File = "", PartKey = "", Res = "";
        public string Hdr = "", Audio = "";
        public int Year;
        public long Duration, Offset, AddedAt;
        public int ViewCount;
        public bool Detailed;
        public List<Chap> Chapters = new List<Chap>();   // 2.57.0
        public string Logo = "";                          // 2.57.0: Plex's clear logo, when it has one
        // 2.68.0: TV: the item's type (movie, show, season, episode) and where it sits.
        public string Type = "movie", ShowTitle = "", ShowKey = "", SeasonKey = "", ShowThumb = "";
        public int SeasonNum, EpNum, LeafCount, ViewedLeafCount;
        public string Poster { get { return Type == "episode" && ShowThumb.Length > 0 ? ShowThumb : Thumb; } }
        public string Code { get { return "S" + SeasonNum.ToString(CultureInfo.InvariantCulture) + " E" + EpNum.ToString(CultureInfo.InvariantCulture); } }
    }

    static class Plex
    {
        public const string Product = "ClipGuard Media Player";

        public static string ClientId = "";

        static Plex()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }   // TLS 1.2
        }

        public static string Http(string method, string url, string token, int timeoutMs)
        {
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = method;
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            rq.Accept = "application/json";
            rq.Headers["X-Plex-Product"] = Product;
            rq.Headers["X-Plex-Version"] = "2.52.0";
            rq.Headers["X-Plex-Client-Identifier"] = ClientId;
            rq.Headers["X-Plex-Platform"] = "Windows";
            rq.Headers["X-Plex-Device"] = "HTPC";
            rq.Headers["X-Plex-Device-Name"] = "ClipGuard";
            if (!string.IsNullOrEmpty(token)) rq.Headers["X-Plex-Token"] = token;
            if (method == "POST" || method == "PUT") rq.ContentLength = 0;
            using (HttpWebResponse r = (HttpWebResponse)rq.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static byte[] Bytes(string url, int timeoutMs)
        {
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            rq.Headers["X-Plex-Client-Identifier"] = ClientId;
            using (HttpWebResponse r = (HttpWebResponse)rq.GetResponse())
            using (Stream s = r.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static object Json(string s)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.DeserializeObject(s);
        }

        public static object Get(object o, string key)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(object o, string key)
        {
            object v = Get(o, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Num(object o, string key)
        {
            object v = Get(o, key);
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }

        public static bool Bool(object o, string key)
        {
            object v = Get(o, key);
            if (v is bool) return (bool)v;
            return v != null && (Convert.ToString(v, CultureInfo.InvariantCulture) == "1" || string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "true", StringComparison.OrdinalIgnoreCase));
        }

        public static object[] Arr(object o, string key)
        {
            object v = Get(o, key);
            object[] a = v as object[];
            if (a != null) return a;
            System.Collections.ArrayList l = v as System.Collections.ArrayList;
            return l != null ? l.ToArray() : new object[0];
        }

        public static Movie ToMovie(object m)
        {
            Movie mv = new Movie();
            mv.Key = Str(m, "ratingKey");
            mv.Title = Str(m, "title");
            mv.Year = (int)Num(m, "year");
            mv.Thumb = Str(m, "thumb");
            mv.Art = Str(m, "art");
            mv.Summary = Str(m, "summary");
            mv.Rating = Str(m, "contentRating");
            mv.Duration = Num(m, "duration");
            mv.Offset = Num(m, "viewOffset");
            mv.ViewCount = (int)Num(m, "viewCount");
            mv.AddedAt = Num(m, "addedAt");
            // 2.68.0: TV
            string ty = Str(m, "type");
            if (ty.Length > 0) mv.Type = ty;
            mv.ShowTitle = Str(m, "grandparentTitle");
            mv.ShowKey = Str(m, "grandparentRatingKey");
            mv.SeasonKey = Str(m, "parentRatingKey");
            mv.ShowThumb = Str(m, "grandparentThumb");
            mv.SeasonNum = (int)Num(m, ty == "season" ? "index" : "parentIndex");
            mv.EpNum = (int)Num(m, "index");
            mv.LeafCount = (int)Num(m, "leafCount");
            mv.ViewedLeafCount = (int)Num(m, "viewedLeafCount");
            FillMedia(mv, m);
            return mv;
        }

        public static void FillMedia(Movie mv, object m)
        {
            object[] media = Arr(m, "Media");
            if (media.Length == 0) return;
            List<Ver> vs = new List<Ver>();
            bool streams = false;
            foreach (object md in media)
            {
                Ver v = new Ver();
                string res = Str(md, "videoResolution");
                v.Res = res == "4k" ? "4K" : (res.Length > 0 && char.IsDigit(res[0]) ? res + "p" : res.ToUpperInvariant());
                object[] parts = Arr(md, "Part");
                if (parts.Length > 0)
                {
                    v.File = Str(parts[0], "file");
                    v.PartKey = Str(parts[0], "key");
                    v.Size = Num(parts[0], "size");
                    v.PartId = Num(parts[0], "id");
                    v.Indexes = Str(parts[0], "indexes").IndexOf("sd", StringComparison.OrdinalIgnoreCase) >= 0;
                    string audio = "", audioAny = "";
                    foreach (object st in Arr(parts[0], "Stream"))
                    {
                        streams = true;
                        long type = Num(st, "streamType");
                        if (type == 1 && v.Hdr.Length == 0)
                        {
                            if (Bool(st, "DOVIPresent")) v.Hdr = "Dolby Vision";
                            else if (Str(st, "colorTrc") == "smpte2084") v.Hdr = "HDR10";
                            else if (Str(st, "colorTrc") == "arib-std-b67") v.Hdr = "HLG";
                            else v.Hdr = "SDR";
                        }
                        else if (type == 2)
                        {
                            string t = AudioName(st);
                            if (audioAny.Length == 0) audioAny = t;
                            if (Bool(st, "selected") && audio.Length == 0) audio = t;
                            string lang = Str(st, "language");
                            v.Audios.Add(new PStream { Id = Num(st, "id"), Index = Get(st, "index") != null ? Num(st, "index") : -1,
                                Name = (lang.Length > 0 ? lang + "  \u00b7  " : "") + t + (Str(st, "title").Length > 0 ? "  \u00b7  " + Str(st, "title") : ""),
                                Selected = Bool(st, "selected") });
                        }
                        else if (type == 3)
                        {
                            string n = Str(st, "extendedDisplayTitle");
                            if (n.Length == 0) n = Str(st, "displayTitle");
                            if (n.Length == 0) n = Str(st, "language");
                            v.Subs.Add(new PStream { Id = Num(st, "id"), Index = Get(st, "index") != null ? Num(st, "index") : -1,
                                Name = n, Selected = Bool(st, "selected"), External = Get(st, "key") != null || Get(st, "index") == null });
                        }
                    }
                    v.Audio = audio.Length > 0 ? audio : audioAny;
                }
                vs.Add(v);
            }
            int keep = mv.Versions.Count == vs.Count ? mv.VerIdx : 0;
            mv.Versions = vs;
            mv.VerIdx = Math.Max(0, Math.Min(vs.Count - 1, keep));
            if (streams) mv.Detailed = true;
            Use(mv);
        }

        // 2.57.0: chapters and the clear logo, from /library/metadata/<key>?includeChapters=1.
        public static void FillExtras(Movie mv, object m)
        {
            List<Chap> cs = new List<Chap>();
            foreach (object c in Arr(m, "Chapter"))
                cs.Add(new Chap { Title = Str(c, "tag"), Thumb = Str(c, "thumb"), Start = Num(c, "startTimeOffset"), End = Num(c, "endTimeOffset") });
            cs.Sort(delegate(Chap a, Chap b) { return a.Start.CompareTo(b.Start); });
            mv.Chapters = cs;
            foreach (object im in Arr(m, "Image"))
                if (Str(im, "type") == "clearLogo" && Str(im, "url").Length > 0) { mv.Logo = Str(im, "url"); break; }
        }

        // The chosen version's badges and file.
        public static void Use(Movie mv)
        {
            if (mv.Versions.Count == 0) return;
            Ver v = mv.Versions[mv.VerIdx];
            mv.Res = v.Res; mv.Hdr = v.Hdr; mv.Audio = v.Audio; mv.PartKey = v.PartKey; mv.File = v.File;
        }

        // 2.52.1: the format, e.g. "TrueHD Atmos 7.1" (displayTitle is often just the language).
        static string AudioName(object st)
        {
            string codec = Str(st, "codec").ToLowerInvariant(), prof = Str(st, "profile").ToLowerInvariant();
            string all = Str(st, "displayTitle") + " " + Str(st, "extendedDisplayTitle") + " " + Str(st, "title");
            string n;
            if (codec == "truehd") n = "TrueHD";
            else if (codec == "eac3") n = "Dolby Digital+";
            else if (codec == "ac3") n = "Dolby Digital";
            else if (codec == "dca" || codec == "dts") n = prof.Contains("ma") ? "DTS-HD MA" : (prof.Contains("hra") ? "DTS-HD HRA" : "DTS");
            else if (codec == "flac") n = "FLAC";
            else if (codec == "aac") n = "AAC";
            else if (codec.StartsWith("pcm", StringComparison.Ordinal)) n = "PCM";
            else n = codec.ToUpperInvariant();
            if (all.IndexOf("DTS:X", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf("DTS-X", StringComparison.OrdinalIgnoreCase) >= 0) n = "DTS:X";
            else if (all.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0) n += " Atmos";
            string lay = Str(st, "audioChannelLayout");
            int p = lay.IndexOf('(');
            if (p > 0) lay = lay.Substring(0, p);
            if (lay.Length == 0 || !char.IsDigit(lay[0]))
            {
                long ch = Num(st, "channels");
                lay = ch == 8 ? "7.1" : ch == 6 ? "5.1" : ch == 2 ? "2.0" : ch == 1 ? "1.0" : "";
            }
            return (n + " " + lay).Trim();
        }
    }

    sealed class MediaPlayer : Form
    {
        // ---- what the host (ClipGuard's control form) gives us ----------------------
        public interface IHost
        {
            int LookAheadFrames();
            void MediaClosed();
        }
#if PLAYER
        // 2.64.1: the player's ClipGuard tab, source and FSRCNNX.
        public interface IPlayerHost
        {
            void SetPlayerSource(int src);   // 0 none, 1 Media Player, 2 Video Processor
            int TabRowCount();
            string TabRowName(int i);
            string TabRowValue(int i);
            void TabRowStep(int i, int dir);
            bool Fsrcnnx();
            void SetFsrcnnx(bool on);
        }
        IPlayerHost PHost { get { return host as IPlayerHost; } }
#endif

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_NOREPEAT = 0x4000;
        const int HK_F4 = 101, HK_L = 102, HK_R = 103, HK_ENTER = 104, HK_ESC = 105;

        static readonly Color Bg = Color.FromArgb(14, 15, 18), Grey = Color.FromArgb(167, 171, 179), Card = Color.FromArgb(34, 37, 43),
            Yellow = Color.FromArgb(255, 212, 0), Dim = Color.FromArgb(42, 45, 51);
        static readonly Color[] Tones = { Color.FromArgb(43, 58, 85), Color.FromArgb(74, 46, 46), Color.FromArgb(46, 74, 58), Color.FromArgb(74, 65, 48), Color.FromArgb(58, 46, 74), Color.FromArgb(46, 63, 74) };
        const int WebPort = 13579;

        readonly IHost host;
        readonly string appDir, seedIni, workRoot, cacheDir;
        string token = "", choice = "all";        // choice: "all" or a server's id
        List<Server> servers = new List<Server>();
        int unpluggedDelay = 100;
#if PLAYER
        // 2.56.0: the player's own lip-sync delay (its picture latency is not Unplugged's).
        const string DelayKey = "PlayerDelay";
        const int DelayMin = -500, DelayDefault = 0;
#else
        const string DelayKey = "UnpluggedDelay";
        const int DelayMin = 0, DelayDefault = 100;
#endif
        static int ClampDelay(int d) { return Math.Max(DelayMin, Math.Min(1000, d)); }

        enum Screen2 { Busy, SignIn, Home, Movies, Details, Settings, VP, Picture, ClipGuard, Shows, Show, Starting }
#if STANDALONE
        const string AppName = "ClipGuard Media Player";
#else
        const string AppName = "ClipGuard+ Media Player";
#endif
        // 2.64.0: the Player has Video Processor (capture) and Picture (its own settings) tabs.
#if PLAYER
        const int TabTV = 2, TabVP = 3, TabPicture = 4, TabClipGuard = 5, TabSettings = 6, TabCount = 7;
        static readonly string[] TabNames = { "Home", "Movies", "TV Shows", "Video Processor", "Picture", "ClipGuard", "Settings" };
#else
        const int TabTV = -1, TabVP = -1, TabPicture = -1, TabClipGuard = -1, TabSettings = 2, TabCount = 3;
        static readonly string[] TabNames = { "Home", "Movies", "Settings" };
#endif
        Screen2 TabScreen(int t)
        {
            if (t == 0) return Screen2.Home;
            if (t == 1) return Screen2.Movies;
            if (t == TabTV) return Screen2.Shows;
            if (t == TabVP) return Screen2.VP;
            if (t == TabPicture) return Screen2.Picture;
            if (t == TabClipGuard) return Screen2.ClipGuard;
            return Screen2.Settings;
        }
        Screen2 scr = Screen2.Busy;
        Screen2 detailsFrom = Screen2.Home;
        string busyText = "Starting the Media Player...";
        string message = "";
        // 2.63.0: Learn remote
        string[] remote;                 // learned buttons ([Remote] in clipguard.ini)
        bool learning;
        int learnIdx;
        string[] learnMap;
        string learnNote = "";
        DateTime learnDeadline;
        System.Windows.Forms.Timer learnTimer;
        const int LearnSkipMs = 8000;
        int tab = 0;               // 0 Home, 1 Movies, 2 Settings
        bool navFocus = false;
        int homeRow = 0;
        int[] homeCol = new int[4], homeScroll = new int[4];   // 2.71.0: four rows (Next Up, Recently Added TV)
        int gridIdx = 0, gridScroll = 0;
        int detBtn = 0;
        int setIdx = 0;
        Movie det;

        List<Movie> movies = new List<Movie>(), cont = new List<Movie>(), recent = new List<Movie>();
        List<Movie> shows = new List<Movie>();   // 2.68.0: TV shows
        List<Movie> nextUp = new List<Movie>(), recentTv = new List<Movie>();   // 2.71.0: Home's TV rows
        Screen2 showFrom = Screen2.Shows;
        int showIdx = 0, showScroll = 0;

        // Sign-in
        string pinCode = "";
        long pinId;
        bool[,] qr;
        System.Windows.Forms.Timer pinTimer;

        // Images
        readonly Dictionary<string, Image> imgs = new Dictionary<string, Image>();
        readonly LinkedList<string> imgOrder = new LinkedList<string>();
        readonly Stack<string> imgWant = new Stack<string>();
        readonly HashSet<string> imgPending = new HashSet<string>();
        readonly Dictionary<string, Server> imgSrv = new Dictionary<string, Server>();
        readonly object imgLock = new object();
        Thread imgThread;
        volatile bool closing;

        // Playing
        Process player;
        Movie playing;
        bool lipSync;
        int lipDelta;
        long lastPos = -1, lastDur;
        int lastState = -1;
        DateTime nextTimeline = DateTime.MinValue;
        System.Windows.Forms.Timer playTimer;
        volatile bool polling;

        public MediaPlayer(IHost host, string appDir, string seedIni, string workRoot)
        {
            this.host = host;
            this.appDir = appDir;
            this.seedIni = seedIni;
            this.workRoot = workRoot;
            cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "PlexCache");
            try { Directory.CreateDirectory(cacheDir); } catch (Exception) { }

            Plex.ClientId = Ini("PlexClientId", "");
            if (Plex.ClientId.Length < 8) { Plex.ClientId = Guid.NewGuid().ToString(); SetIni("PlexClientId", Plex.ClientId); }
            token = Ini("PlexToken", "");
            choice = Ini("PlexServerChoice", "all");
            if (choice.Length == 0) choice = "all";
            int d;
            unpluggedDelay = DelayDefault;
            if (int.TryParse(Ini(DelayKey, DelayDefault.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) unpluggedDelay = ClampDelay(d);
            remote = Remote.Load(seedIni);

            Text = "ClipGuard Media Player";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = UnpluggedScreen().Bounds;   // real pixels: ClipGuard is DPI aware in the Test build (2.52.1)
            BackColor = Bg;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            playTimer = new System.Windows.Forms.Timer();
            playTimer.Interval = 1000;
            playTimer.Tick += delegate { PlayTick(); };
#if PLAYER
            if (PHost != null) { IPlayerHost ph = PHost; UnpluggedSettings.GetFsr = ph.Fsrcnnx; UnpluggedSettings.SetFsr = ph.SetFsrcnnx; }
            // 2.66.0: the LUT moved from ClipGuard's LUT row to Picture > Calibration LUT; the
            // Titan / Valerion LUT that was chosen there is chosen here, once.
            try
            {
                if (Ini("LutMoved", "0") != "1")
                {
                    SetIni("LutMoved", "1");
                    string cg = Native.ReadIni(seedIni, "ClipGuard", "Lut", "0").Trim();
                    string want = cg == "1" ? "titan.cube" : (cg == "2" ? "valerion.cube" : "");
                    if (want.Length > 0 && UnpluggedSettings.LutFile().Length == 0)
                    {
                        string f = LutChoices().Find(delegate(string x) { return string.Equals(Path.GetFileName(x), want, StringComparison.OrdinalIgnoreCase); });
                        if (f != null) UnpluggedSettings.SetLut(f);
                    }
                }
            }
            catch (Exception) { }
#if STANDALONE
            // 2.67.0: a Titan / Valerion LUT chosen from ClipGuard's old folders is used from the
            // Calibration folder (the first start copied it there), so nothing outside is needed.
            try
            {
                string cur = UnpluggedSettings.LutFile();
                if (cur.Length > 0 && !string.Equals(Path.GetDirectoryName(cur), CalDir, StringComparison.OrdinalIgnoreCase))
                {
                    string mine = Path.Combine(CalDir, Path.GetFileName(cur));
                    if (!File.Exists(mine) && File.Exists(cur)) File.Copy(cur, mine);
                    if (File.Exists(mine)) UnpluggedSettings.SetLut(mine);
                }
            }
            catch (Exception) { }
#endif
            // 2.63.6: the player keeps the keyboard. When another program's window takes it
            // while the player covers the screen, it comes back (not while a panel is open).
            focusTimer = new System.Windows.Forms.Timer();
            focusTimer.Interval = 300;
            focusTimer.Tick += delegate { KeepFocus(); };
            focusTimer.Start();
#endif

            imgThread = new Thread(ImgLoop);
            imgThread.IsBackground = true;
            imgThread.Start();
        }

        System.Windows.Forms.Timer focusTimer;
        bool panelOpen;
#if PLAYER

        void KeepFocus()
        {
            if (panelOpen) return;
            Form want = (video != null && video.Visible) ? (Form)video : (Visible ? (Form)this : null);
            if (want == null || !want.IsHandleCreated) return;
            IntPtr fg = GetForegroundWindow();
            uint pid = 0;
            if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
            bool overlayHasIt = ovl != null && ovl.IsHandleCreated && fg == ovl.Handle;
            if (!overlayHasIt && fg != IntPtr.Zero && pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
            SetForegroundWindow(want.Handle);
            want.Activate();
        }
#endif

        // The settings panel owned by this window (a visible top-level window it owns).
        static IntPtr OwnedPanel(IntPtr owner)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == owner) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // ---- settings in clipguard.ini next to ClipGuard.exe ([MediaPlayer]) --------
        string Ini(string key, string def) { return Native.ReadIni(seedIni, "MediaPlayer", key, def).Trim(); }
        void SetIni(string key, string val) { try { Native.WritePrivateProfileString("MediaPlayer", key, val, seedIni); } catch (Exception) { } }

        static System.Windows.Forms.Screen UnpluggedScreen()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("Unplugged"))
                    if (p.MainWindowHandle != IntPtr.Zero) return System.Windows.Forms.Screen.FromHandle(p.MainWindowHandle);
            }
            catch (Exception) { }
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        bool cursorHidden;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
#if PLAYER
            try
            {
                backdrop = new Backdrop(this, Bounds);
                backdrop.Show();
                BringToFront();
            }
            catch (Exception) { }
#endif
            Activate();
            Cursor.Hide();
            cursorHidden = true;
            // Video Processor stops while the library is up (and a film needs Unplugged closed first).
            Bg2(delegate { CloseUnplugged(); }, delegate { Start(); });
        }

        bool startedOnce;
        void Start()
        {
#if PLAYER
            // 2.64.0: Picture > Start in: Video Processor starts the capture at once; the
            // library loads behind it.
            if (!startedOnce) { startedOnce = true; if (Ini("StartIn", "Library") == "VideoProcessor") BeginInvoke((MethodInvoker)delegate { StartCapture(); }); }
#endif
            if (token.Length == 0) { BeginSignIn(); return; }
            // 2.55.0: the servers that answered last time, and the library as it was, at once;
            // Plex is asked again in the background.
            List<Server> known = LoadServerCache();
            if (known.Count == 0) { FindServers(); return; }
            servers = known;
            if (choice != "all" && servers.Find(delegate(Server x) { return x.Id == choice; }) == null) choice = "all";
            if (LoadLibraryCache())
            {
                scr = Screen2.Home; tab = 0; homeRow = HomeRows()[0];
                Invalidate();
                CheckServersThenRefresh();
            }
            else CheckServersThenRefresh();
        }

        // Each remembered server is asked once (4 s); if one doesn't answer, the full
        // search through plex.tv runs, as before.
        void CheckServersThenRefresh()
        {
            bool showing = scr == Screen2.Home || scr == Screen2.Movies;
            if (!showing) Busy("Connecting to your Plex servers...");
            List<Server> use = new List<Server>(servers);
            bool allOk = true;
            Bg2(delegate
            {
                List<Thread> ts = new List<Thread>();
                foreach (Server sv0 in use)
                {
                    Server sv = sv0;
                    Thread th = new Thread(delegate()
                    {
                        try { Plex.Http("GET", sv.Uri + "/identity", sv.Token, 4000); }
                        catch (Exception) { allOk = false; }
                    });
                    th.IsBackground = true; th.Start(); ts.Add(th);
                }
                foreach (Thread th in ts) th.Join(6000);
            }, delegate
            {
                if (!allOk) { if (showing) FindServersQuiet(); else FindServers(); return; }
                LoadLibrary(showing);
            });
        }

        void FindServersQuiet()
        {
            // The cached library stays on screen while plex.tv is asked.
            Screen2 keep = scr;
            FindServers();
            if (movies.Count > 0) { scr = keep; Invalidate(); }
        }

        void SaveServerCache()
        {
            SetIni("PlexServerCount", servers.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < servers.Count; i++)
            {
                Server sv = servers[i];
                SetIni("PlexSrv" + i.ToString(CultureInfo.InvariantCulture), string.Join("|", new[] {
                    Uri.EscapeDataString(sv.Id), Uri.EscapeDataString(sv.Name), Uri.EscapeDataString(sv.Uri), Uri.EscapeDataString(sv.Token), sv.Owned ? "1" : "0" }));
            }
        }

        List<Server> LoadServerCache()
        {
            List<Server> l = new List<Server>();
            int n;
            if (!int.TryParse(Ini("PlexServerCount", "0"), out n)) return l;
            for (int i = 0; i < n && i < 16; i++)
            {
                StringBuilder sb = new StringBuilder(2048);
                GetPrivateProfileString("MediaPlayer", "PlexSrv" + i.ToString(CultureInfo.InvariantCulture), "", sb, sb.Capacity, seedIni);
                string[] f = sb.ToString().Split('|');
                if (f.Length < 5) continue;
                Server sv = new Server();
                sv.Id = Uri.UnescapeDataString(f[0]); sv.Name = Uri.UnescapeDataString(f[1]); sv.Uri = Uri.UnescapeDataString(f[2]);
                sv.Token = Uri.UnescapeDataString(f[3]); sv.Owned = f[4] == "1";
                if (sv.Id.Length > 0 && sv.Uri.Length > 0) l.Add(sv);
            }
            return l;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder ret, int size, string file);

        // The library list on disk (no tokens: those stay in clipguard.ini).
        string LibraryCacheFile { get { return Path.Combine(cacheDir, "library.json"); } }

        static Dictionary<string, object> Pack(Movie m)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["s"] = m.Srv.Id; d["k"] = m.Key; d["t"] = m.Title; d["th"] = m.Thumb; d["a"] = m.Art; d["su"] = m.Summary; d["r"] = m.Rating;
            d["y"] = m.Year; d["du"] = m.Duration; d["o"] = m.Offset; d["v"] = m.ViewCount; d["ad"] = m.AddedAt;
            d["re"] = m.Res; d["h"] = m.Hdr; d["au"] = m.Audio;
            d["ty"] = m.Type; d["st"] = m.ShowTitle; d["sk"] = m.ShowKey; d["pk"] = m.SeasonKey; d["sth"] = m.ShowThumb;
            d["sn"] = m.SeasonNum; d["en"] = m.EpNum; d["lc"] = m.LeafCount; d["vl"] = m.ViewedLeafCount;
            return d;
        }

        Movie Unpack(object o)
        {
            string sid = Plex.Str(o, "s");
            Server sv = servers.Find(delegate(Server x) { return x.Id == sid; });
            if (sv == null) return null;
            Movie m = new Movie();
            m.Srv = sv; m.Key = Plex.Str(o, "k"); m.Title = Plex.Str(o, "t"); m.Thumb = Plex.Str(o, "th"); m.Art = Plex.Str(o, "a");
            m.Summary = Plex.Str(o, "su"); m.Rating = Plex.Str(o, "r"); m.Year = (int)Plex.Num(o, "y"); m.Duration = Plex.Num(o, "du");
            m.Offset = Plex.Num(o, "o"); m.ViewCount = (int)Plex.Num(o, "v"); m.AddedAt = Plex.Num(o, "ad");
            m.Res = Plex.Str(o, "re"); m.Hdr = Plex.Str(o, "h"); m.Audio = Plex.Str(o, "au");
            string ty = Plex.Str(o, "ty"); if (ty.Length > 0) m.Type = ty;
            m.ShowTitle = Plex.Str(o, "st"); m.ShowKey = Plex.Str(o, "sk"); m.SeasonKey = Plex.Str(o, "pk"); m.ShowThumb = Plex.Str(o, "sth");
            m.SeasonNum = (int)Plex.Num(o, "sn"); m.EpNum = (int)Plex.Num(o, "en"); m.LeafCount = (int)Plex.Num(o, "lc"); m.ViewedLeafCount = (int)Plex.Num(o, "vl");
            return m;
        }

        void SaveLibraryCache()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["choice"] = choice;
                foreach (KeyValuePair<string, List<Movie>> kv in new[] {
                    new KeyValuePair<string, List<Movie>>("movies", movies), new KeyValuePair<string, List<Movie>>("cont", cont), new KeyValuePair<string, List<Movie>>("recent", recent),
                    new KeyValuePair<string, List<Movie>>("shows", shows), new KeyValuePair<string, List<Movie>>("nextup", nextUp),
                    new KeyValuePair<string, List<Movie>>("recenttv", recentTv) })
                {
                    List<object> l = new List<object>();
                    foreach (Movie m in kv.Value) l.Add(Pack(m));
                    d[kv.Key] = l;
                }
                JavaScriptSerializer js = new JavaScriptSerializer();
                js.MaxJsonLength = int.MaxValue;
                File.WriteAllText(LibraryCacheFile, js.Serialize(d), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        bool LoadLibraryCache()
        {
            try
            {
                if (!File.Exists(LibraryCacheFile)) return false;
                object d = Plex.Json(File.ReadAllText(LibraryCacheFile, Encoding.UTF8));
                if (Plex.Str(d, "choice") != choice) return false;
                List<Movie>[] ls = { new List<Movie>(), new List<Movie>(), new List<Movie>(), new List<Movie>(), new List<Movie>(), new List<Movie>() };
                string[] keys = { "movies", "cont", "recent", "shows", "nextup", "recenttv" };
                for (int i = 0; i < keys.Length; i++)
                    foreach (object o in Plex.Arr(d, keys[i])) { Movie m = Unpack(o); if (m != null) ls[i].Add(m); }
                if (ls[0].Count == 0 && ls[3].Count == 0) return false;
                movies = ls[0]; cont = ls[1]; recent = ls[2]; shows = ls[3]; nextUp = ls[4]; recentTv = ls[5];
                return true;
            }
            catch (Exception) { return false; }
        }

        void Busy(string text) { scr = Screen2.Busy; busyText = text; Invalidate(); }

        // Runs work on a background thread, then done on this one (or fail, else the error box).
        void Bg2(Action work, Action done) { Bg2(work, done, null); }

        void Bg2(Action work, Action done, Action<Exception> fail)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception err = null;
                try { work(); } catch (Exception ex) { err = ex; }
                if (closing) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (err != null) { if (fail != null) fail(err); else ShowError(err); return; }
                        if (done != null) done();
                    });
                }
                catch (Exception) { }
            });
        }

        void ShowError(Exception ex)
        {
            string m = ex.Message;
            WebException we = ex as WebException;
            if (we != null && we.Response is HttpWebResponse && ((HttpWebResponse)we.Response).StatusCode == HttpStatusCode.Unauthorized)
                m = "Plex did not accept the sign-in. Sign out in Settings and sign in again.";
            message = m;
            if (scr == Screen2.Busy) scr = token.Length == 0 ? Screen2.SignIn : Screen2.Settings;
            if (scr == Screen2.Settings) tab = TabSettings;
            Invalidate();
        }

        // ---- sign-in: two Plex PINs (2.52.1) ---------------------------------------------
        // The short code is for plex.tv/link; the app.plex.tv link in the QR code
        // needs a long ("strong") one. Either one signing in is enough.
        long pinId2;
        void BeginSignIn()
        {
            Busy("Asking Plex for a sign-in code...");
            string json = null, json2 = null;
            Bg2(delegate
            {
                json = Plex.Http("POST", "https://plex.tv/api/v2/pins", null, 15000);
                try { json2 = Plex.Http("POST", "https://plex.tv/api/v2/pins?strong=true", null, 15000); } catch (Exception) { json2 = null; }
            }, delegate
            {
                object o = Plex.Json(json);
                pinId = Plex.Num(o, "id");
                pinCode = Plex.Str(o, "code");
                qr = null;
                pinId2 = 0;
                if (json2 != null)
                {
                    object o2 = Plex.Json(json2);
                    pinId2 = Plex.Num(o2, "id");
                    string url = "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(Plex.ClientId) + "&code=" + Uri.EscapeDataString(Plex.Str(o2, "code"))
                        + "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(Plex.Product);
                    try { qr = Qr.Encode(url, -1); } catch (Exception) { qr = null; }
                }
                pinStarted = DateTime.Now;
                scr = Screen2.SignIn;
                message = "";
                Invalidate();
                if (pinTimer == null)
                {
                    pinTimer = new System.Windows.Forms.Timer();
                    pinTimer.Interval = 2000;
                    pinTimer.Tick += delegate { PollPin(); };
                }
                pinTimer.Start();
            });
        }

        volatile bool pinBusy;
        DateTime pinStarted = DateTime.Now;
        void PollPin()
        {
            if (scr != Screen2.SignIn || pinBusy) return;
            if ((DateTime.Now - pinStarted).TotalMinutes > 14) { pinTimer.Stop(); BeginSignIn(); return; }
            pinBusy = true;
            string t = "";
            long a = pinId, b = pinId2;
            Bg2(delegate
            {
                foreach (long id in new[] { a, b })
                {
                    if (id == 0 || t.Length > 0) continue;
                    try { t = Plex.Str(Plex.Json(Plex.Http("GET", "https://plex.tv/api/v2/pins/" + id.ToString(CultureInfo.InvariantCulture), null, 10000)), "authToken"); }
                    catch (Exception) { }
                }
            }, delegate
            {
                pinBusy = false;
                if (t.Length == 0) return;
                pinTimer.Stop();
                token = t;
                SetIni("PlexToken", token);
                FindServers();
            }, delegate(Exception ex) { pinBusy = false; });
        }

        // ---- the servers: every Plex Media Server of the account that answers (2.52.1) -------
        // Your own servers first. Each is tried at its local addresses first, all at once.
        void FindServers()
        {
            Busy("Looking for your Plex servers...");
            List<Server> found = new List<Server>();
            Bg2(delegate
            {
                object res = Plex.Json(Plex.Http("GET", "https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=0", token, 15000));
                object[] list = res as object[];
                if (list == null && res is System.Collections.ArrayList) list = ((System.Collections.ArrayList)res).ToArray();
                if (list == null) list = new object[0];
                List<Thread> ts = new List<Thread>();
                foreach (object r0 in list)
                {
                    object r = r0;
                    if (!Plex.Str(r, "provides").Contains("server")) continue;
                    Thread th = new Thread(delegate()
                    {
                        string tk = Plex.Str(r, "accessToken");
                        if (tk.Length == 0) tk = token;
                        List<string> tries = new List<string>();
                        foreach (object c in Plex.Arr(r, "connections"))
                            if (Plex.Bool(c, "local"))
                            {
                                tries.Add("http://" + Plex.Str(c, "address") + ":" + Plex.Str(c, "port"));
                                tries.Add(Plex.Str(c, "uri"));
                            }
                        foreach (object c in Plex.Arr(r, "connections"))
                            if (!Plex.Bool(c, "local") && !Plex.Bool(c, "relay")) tries.Add(Plex.Str(c, "uri"));
                        foreach (string u in tries)
                        {
                            try
                            {
                                Plex.Http("GET", u + "/identity", tk, 4000);
                                Server sv = new Server();
                                sv.Name = Plex.Str(r, "name"); sv.Id = Plex.Str(r, "clientIdentifier"); sv.Uri = u; sv.Token = tk; sv.Owned = Plex.Bool(r, "owned");
                                lock (found) found.Add(sv);
                                return;
                            }
                            catch (Exception) { }
                        }
                    });
                    th.IsBackground = true;
                    th.Start();
                    ts.Add(th);
                }
                foreach (Thread th in ts) th.Join(30000);
                if (found.Count == 0) throw new Exception("No Plex server answered. Check that your Plex PC is on, then choose Plex server in Settings and press Enter to look again.");
                found.Sort(delegate(Server x, Server y) { return x.Owned != y.Owned ? (x.Owned ? -1 : 1) : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); });
            }, delegate
            {
                servers = found;
                if (choice != "all" && servers.Find(delegate(Server x) { return x.Id == choice; }) == null) choice = "all";
                SaveServerCache();
                LoadLibrary(movies.Count > 0 && (scr == Screen2.Home || scr == Screen2.Movies));
            });
        }

        List<Server> Chosen()
        {
            if (choice == "all") return new List<Server>(servers);
            return servers.FindAll(delegate(Server x) { return x.Id == choice; });
        }

        static string Url(Server s, string path) { return s.Uri + path; }

        // ---- the library -----------------------------------------------------------
        void LoadLibrary() { LoadLibrary(false); }

        // quiet: the cached library stays on screen and is swapped for the fresh one.
        void LoadLibrary(bool quiet)
        {
            if (!quiet) Busy("Loading your movies...");
            List<Movie> all = new List<Movie>(), c = new List<Movie>(), rec = new List<Movie>(), tv = new List<Movie>(), rtv = new List<Movie>();
            List<Server> use = Chosen();
            Bg2(delegate
            {
                foreach (Server sv in use)
                {
                    try
                    {
                        object secs = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/library/sections"), sv.Token, 15000)), "MediaContainer");
                        foreach (object d in Plex.Arr(secs, "Directory"))
                        {
                            string k = Plex.Str(d, "key");
                            if (Plex.Str(d, "type") == "show")
                            {
                                // 2.68.0: TV shows
                                try
                                {
                                    object sc = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/library/sections/" + k + "/all?type=2&sort=titleSort"), sv.Token, 60000)), "MediaContainer");
                                    foreach (object m in Plex.Arr(sc, "Metadata")) { Movie mv = Plex.ToMovie(m); mv.Srv = sv; mv.Type = "show"; tv.Add(mv); }
                                }
                                catch (Exception) { }
                                // 2.71.0: Recently Added TV (Plex groups a season added at once into one item)
                                try
                                {
                                    object ra = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/library/sections/" + k + "/recentlyAdded?X-Plex-Container-Start=0&X-Plex-Container-Size=30"), sv.Token, 15000)), "MediaContainer");
                                    foreach (object m in Plex.Arr(ra, "Metadata")) { Movie mv = RecentTvItem(m); if (mv != null) { mv.Srv = sv; rtv.Add(mv); } }
                                }
                                catch (Exception) { }
                                continue;
                            }
                            if (Plex.Str(d, "type") != "movie") continue;
                            object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/library/sections/" + k + "/all?type=1&sort=titleSort"), sv.Token, 60000)), "MediaContainer");
                            foreach (object m in Plex.Arr(mc, "Metadata")) { Movie mv = Plex.ToMovie(m); mv.Srv = sv; all.Add(mv); }
                            try
                            {
                                object rc = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/library/sections/" + k + "/recentlyAdded?X-Plex-Container-Start=0&X-Plex-Container-Size=30"), sv.Token, 15000)), "MediaContainer");
                                foreach (object m in Plex.Arr(rc, "Metadata")) if (Plex.Str(m, "type") == "movie") { Movie mv = Plex.ToMovie(m); mv.Srv = sv; rec.Add(mv); }
                            }
                            catch (Exception) { }
                        }
                        LoadContinue(sv, c);
                    }
                    catch (Exception) { if (use.Count == 1) throw; }
                }
                rec.Sort(delegate(Movie a, Movie b) { return b.AddedAt.CompareTo(a.AddedAt); });
                if (rec.Count > 30) rec.RemoveRange(30, rec.Count - 30);
                rtv.Sort(delegate(Movie a, Movie b) { return b.AddedAt.CompareTo(a.AddedAt); });
                if (rtv.Count > 30) rtv.RemoveRange(30, rtv.Count - 30);
            }, delegate
            {
                all.Sort(delegate(Movie a, Movie b) { return string.Compare(SortTitle(a.Title), SortTitle(b.Title), StringComparison.OrdinalIgnoreCase); });
                tv.Sort(delegate(Movie a, Movie b) { return string.Compare(SortTitle(a.Title), SortTitle(b.Title), StringComparison.OrdinalIgnoreCase); });
                movies = all; recent = rec; shows = tv; recentTv = rtv;
                SplitContinue(c);
                showIdx = Math.Min(showIdx, Math.Max(0, shows.Count - 1));
                SaveLibraryCache();
                if (quiet)
                {
                    // Keep where you are; only the lists change.
                    gridIdx = Math.Min(gridIdx, Math.Max(0, movies.Count - 1));
                    for (int r = 0; r < 4; r++) homeCol[r] = Math.Min(homeCol[r], Math.Max(0, HomeList(r).Count - 1));
                    if (HomeRows().IndexOf(homeRow) < 0) homeRow = HomeRows()[0];
                    if (scr == Screen2.Details && det != null)
                    {
                        Movie fresh = movies.Find(delegate(Movie x) { return x.Srv.Id == det.Srv.Id && x.Key == det.Key; });
                        if (fresh != null && !fresh.Detailed && det.Detailed) { fresh.Versions = det.Versions; fresh.VerIdx = det.VerIdx; fresh.Detailed = true; Plex.Use(fresh); }
                    }
                    Invalidate();
                    return;
                }
                scr = TabScreen(tab);
                if (tab == 0) homeRow = HomeRows()[0];
                Invalidate();
            }, quiet ? (Action<Exception>)delegate(Exception ex) { } : null);
        }

        // 2.71.0: Continue Watching keeps what you are partway through; an episode not started
        // (the next one of a show you watch) goes to Next Up.
        void SplitContinue(List<Movie> c)
        {
            List<Movie> a = new List<Movie>(), n = new List<Movie>();
            foreach (Movie m in c) { if (m.Type == "episode" && m.Offset <= 0) n.Add(m); else a.Add(m); }
            cont = a; nextUp = n;
        }

        // A recently added TV item: an episode as it is, a season (or show) as its show.
        static Movie RecentTvItem(object m)
        {
            string ty = Plex.Str(m, "type");
            if (ty == "episode") return Plex.ToMovie(m);
            if (ty != "season" && ty != "show") return null;
            Movie s = Plex.ToMovie(m);
            if (ty == "season")
            {
                s.Key = Plex.Str(m, "parentRatingKey");
                s.Title = Plex.Str(m, "parentTitle");
                string pt = Plex.Str(m, "parentThumb");
                if (pt.Length > 0) s.Thumb = pt;
                s.SeasonNum = (int)Plex.Num(m, "index");
            }
            s.Type = "show";
            return s.Key.Length > 0 ? s : null;
        }

        static void LoadContinue(Server sv, List<Movie> c)
        {
            object mc = null;
            try { mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/hubs/continueWatching/items"), sv.Token, 15000)), "MediaContainer"); }
            catch (Exception) { }
            if (mc == null) mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/library/onDeck"), sv.Token, 15000)), "MediaContainer");
            foreach (object m in Plex.Arr(mc, "Metadata"))
            {
                string ty = Plex.Str(m, "type");
                if (ty == "movie" || ty == "episode") { Movie mv = Plex.ToMovie(m); mv.Srv = sv; c.Add(mv); }   // 2.68.0: and episodes
            }
        }

        void RefreshContinue()
        {
            List<Movie> c = new List<Movie>();
            List<Server> use = Chosen();
            Bg2(delegate { foreach (Server sv in use) { try { LoadContinue(sv, c); } catch (Exception) { } } }, delegate
            {
                SplitContinue(c);   // 2.71.0
                // Keep the grid's resume points in step.
                Dictionary<string, Movie> byKey = new Dictionary<string, Movie>();
                foreach (Movie m in c) byKey[m.Srv.Id + "/" + m.Key] = m;
                foreach (Movie m in movies) { Movie u; if (byKey.TryGetValue(m.Srv.Id + "/" + m.Key, out u)) m.Offset = u.Offset; }
                homeCol[0] = Math.Min(homeCol[0], Math.Max(0, cont.Count - 1));
                homeCol[2] = Math.Min(homeCol[2], Math.Max(0, nextUp.Count - 1));
                if (HomeRows().IndexOf(homeRow) < 0) homeRow = HomeRows()[0];
                Invalidate();
            });
        }

        static string SortTitle(string t)
        {
            foreach (string a in new[] { "The ", "A ", "An " })
                if (t.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return t.Substring(a.Length);
            return t;
        }

        // ---- images: posters and backdrops from the server, cached on the HTPC ---------
        Image Img(Movie m, string path, int w, int h)
        {
            if (string.IsNullOrEmpty(path) || m.Srv == null) return null;
            string key = m.Srv.Id + "|" + path + "@" + w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture);
            lock (imgLock)
            {
                Image im;
                if (imgs.TryGetValue(key, out im)) return im;
                if (!imgPending.Contains(key)) { imgPending.Add(key); imgSrv[key] = m.Srv; imgWant.Push(key); Monitor.Pulse(imgLock); }
            }
            return null;
        }

        void ImgLoop()
        {
            while (!closing)
            {
                string key;
                Server sv;
                lock (imgLock)
                {
                    while (imgWant.Count == 0 && !closing) Monitor.Wait(imgLock, 1000);
                    if (closing) return;
                    key = imgWant.Pop();
                    sv = imgSrv[key];
                }
                Image im = null;
                try
                {
                    int at = key.LastIndexOf('@'), bar = key.IndexOf('|');
                    string path = key.Substring(bar + 1, at - bar - 1);
                    string[] wh = key.Substring(at + 1).Split('x');
                    string file = Path.Combine(cacheDir, Hash(key) + ".jpg");
                    byte[] b = null;
                    if (File.Exists(file)) b = File.ReadAllBytes(file);
                    else
                    {
                        b = Plex.Bytes(sv.Uri + "/photo/:/transcode?width=" + wh[0] + "&height=" + wh[1] + "&minSize=1&upscale=1&url=" + Uri.EscapeDataString(path)
                            + "&X-Plex-Token=" + Uri.EscapeDataString(sv.Token), 15000);
                        try { File.WriteAllBytes(file, b); } catch (Exception) { }
                    }
                    using (MemoryStream ms = new MemoryStream(b))
                    using (Image tmp = Image.FromStream(ms))
                        im = new Bitmap(tmp);
                }
                catch (Exception) { }
                lock (imgLock)
                {
                    imgPending.Remove(key);
                    imgSrv.Remove(key);
                    if (im != null)
                    {
                        imgs[key] = im;
                        imgOrder.AddLast(key);
                        while (imgOrder.Count > 140)
                        {
                            string old = imgOrder.First.Value;
                            imgOrder.RemoveFirst();
                            Image o;
                            if (imgs.TryGetValue(old, out o)) { imgs.Remove(old); o.Dispose(); }
                        }
                    }
                }
                if (im != null && !closing) { try { BeginInvoke((Action)Invalidate); } catch (Exception) { } }
            }
        }

        static string Hash(string s)
        {
            using (System.Security.Cryptography.SHA1 h = System.Security.Cryptography.SHA1.Create())
            {
                byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 10; i++) sb.Append(d[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // ---- keys ------------------------------------------------------------------
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (learning) { LearnCode(keyData, Remote.OfKey(keyData)); Invalidate(); return true; }
#if PLAYER
            if (calState != 0) { CalDlgKey(keyData); Invalidate(); return true; }   // typing: no remote mapping
#endif
            if (OnKey(Remote.Translate(remote, keyData))) { Invalidate(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Learned buttons, for the video window too.
        public Keys MapKey(Keys k) { return Remote.Translate(remote, k); }
        public Keys MapApp(int cmd) { return Remote.TranslateApp(remote, cmd); }

        // ---- 2.63.0: Learn remote ------------------------------------------------------
        void StartLearn()
        {
            learnMap = new string[Remote.Actions.Length];
            learnIdx = 0;
            learnNote = "";
            learning = true;
            learnDeadline = DateTime.Now.AddMilliseconds(LearnSkipMs);
            if (learnTimer == null)
            {
                learnTimer = new System.Windows.Forms.Timer();
                learnTimer.Interval = 200;
                learnTimer.Tick += delegate
                {
                    if (!learning) { learnTimer.Stop(); return; }
                    if (DateTime.Now >= learnDeadline) { learnMap[learnIdx] = ""; LearnNext(); }
                    Invalidate();
                };
            }
            learnTimer.Start();
            Invalidate();
        }

        void LearnCode(Keys k, string code)
        {
            Keys kc = k & Keys.KeyCode;
            if (kc == Keys.ControlKey || kc == Keys.ShiftKey || kc == Keys.Menu || kc == Keys.LWin || kc == Keys.RWin || kc == Keys.None) return;
            int used = Remote.Find(learnMap, code);
            if (used >= 0)
            {
                learnNote = "That button is already " + Remote.Names[used] + ". Press another one, or wait to skip.";
                learnDeadline = DateTime.Now.AddMilliseconds(LearnSkipMs);
                return;
            }
            learnMap[learnIdx] = code;
            LearnNext();
        }

        void LearnNext()
        {
            learnNote = "";
            learnIdx++;
            learnDeadline = DateTime.Now.AddMilliseconds(LearnSkipMs);
            if (learnIdx < Remote.Actions.Length) return;
            learning = false;
            if (learnTimer != null) learnTimer.Stop();
            Remote.Save(seedIni, learnMap);
            remote = learnMap;
            List<string> skipped = new List<string>();
            for (int i = 0; i < learnMap.Length; i++) if (string.IsNullOrEmpty(learnMap[i])) skipped.Add(Remote.Names[i]);
            int got = learnMap.Length - skipped.Count;
            message = "Remote learned: " + got.ToString(CultureInfo.InvariantCulture) + " of " + learnMap.Length.ToString(CultureInfo.InvariantCulture) + " buttons."
                + (skipped.Count > 0 ? "  Skipped: " + string.Join(", ", skipped.ToArray()) + "." : "")
                + "  The keyboard keys still work too. ClipGuard uses the Menu button and, in its menu, the arrows, OK and Back, from its next start.";
            Invalidate();
        }

        void DrawLearn(Graphics g)
        {
            RectangleF r = new RectangleF(360, 330, 1200, 420);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 30, 32, 38))) g.FillRectangle(b, r);
            using (Pen p = new Pen(Yellow, 4)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            Txt(g, "Learn remote   " + (learnIdx + 1).ToString(CultureInfo.InvariantCulture) + " of " + Remote.Actions.Length.ToString(CultureInfo.InvariantCulture), 24, false, Grey, r.X + 40, r.Y + 30);
            Txt(g, "Press the button for", 30, false, Color.White, r.X + 40, r.Y + 90);
            TextBox(g, Remote.Names[Math.Min(learnIdx, Remote.Names.Length - 1)], 48, true, Yellow, new RectangleF(r.X + 40, r.Y + 140, r.Width - 80, 80));
            if (learnNote.Length > 0) TextBox(g, learnNote, 22, false, Color.White, new RectangleF(r.X + 40, r.Y + 240, r.Width - 80, 70));
            int left = Math.Max(0, (int)Math.Ceiling((learnDeadline - DateTime.Now).TotalSeconds));
            Txt(g, "No such button? Wait " + left.ToString(CultureInfo.InvariantCulture) + " s to skip it.", 22, false, Grey, r.X + 40, r.Bottom - 60);
        }

        bool OnKey(Keys k)
        {
#if PLAYER
            if (calState != 0) return CalDlgKey(k);   // 2.66.0: the calibration dialog has the keyboard
            // 2.53.1: Esc (or Alt+F4) shuts the player down from anywhere; Backspace is "back".
            if (k == Keys.Escape || k == (Keys.Alt | Keys.F4)) { Quit(); return true; }
#endif
#if PLAYER
            // 2.64.0: F4 in the library is Video Processor (capture), as its help line says.
            if (k == Keys.F4) { if (scr != Screen2.Busy && scr != Screen2.Starting) StartCapture(); return true; }
            // 2.70.0: a film starting: Back cancels it.
            if (scr == Screen2.Starting)
            {
                if (k == Keys.Back || k == Keys.BrowserBack || k == Keys.MediaStop) CancelStarting();
                return true;
            }
#else
            if (k == Keys.F4) { if (scr != Screen2.Busy) ExitToProcessor(); return true; }
#endif
            if (scr == Screen2.Busy) return true;
#if PLAYER
            bool back = k == Keys.Back || k == Keys.BrowserBack;
#else
            bool back = k == Keys.Escape || k == Keys.Back || k == Keys.BrowserBack;
#endif
            if (message.Length > 0 && (k == Keys.Enter || back)) { message = ""; return true; }
            if (scr == Screen2.SignIn)
            {
                if (back) { ExitToProcessor(); return true; }
                return true;
            }
            if (scr == Screen2.Details) return DetailsKey(k, back);
#if PLAYER
            if (scr == Screen2.Show) return ShowKey(k, back);
#endif
            if (navFocus)
            {
                if (k == Keys.Left) tab = (tab + TabCount - 1) % TabCount;
                else if (k == Keys.Right) tab = (tab + 1) % TabCount;
                else if (k == Keys.Down || k == Keys.Enter)
                {
#if PLAYER
                    if (tab == TabVP && k == Keys.Enter) { StartCapture(); return true; }
#endif
                    navFocus = false;
                }
                else if (back) { ExitToProcessor(); return true; }
                scr = TabScreen(tab);
                return true;
            }
#if PLAYER
            if (back && PictureBack()) return true;
#endif
            if (back) { navFocus = true; return true; }
            switch (scr)
            {
                case Screen2.Home: return HomeKey(k);
                case Screen2.Movies: return GridKey(k);
                case Screen2.Shows: return GridKey(k);
                case Screen2.Settings: return SettingsKey(k);
#if PLAYER
                case Screen2.VP: if (k == Keys.Enter) StartCapture(); else if (k == Keys.Up) navFocus = true; return true;
                case Screen2.Picture: return PictureKey(k);
                case Screen2.ClipGuard: return CgTabKey(k);
#endif
            }
            return false;
        }

        // 0 Continue Watching, 1 Recently Added Movies, 2 Next Up, 3 Recently Added TV (2.71.0)
        List<Movie> HomeList(int r) { return r == 0 ? cont : (r == 1 ? recent : (r == 2 ? nextUp : recentTv)); }

        // The rows shown: the movie row always, the others when they have something.
        List<int> HomeRows()
        {
            List<int> l = new List<int>();
            if (cont.Count > 0) l.Add(0);
            l.Add(1);
            if (nextUp.Count > 0) l.Add(2);
            if (recentTv.Count > 0) l.Add(3);
            return l;
        }

        bool HomeKey(Keys k)
        {
            List<int> rows = HomeRows();
            if (rows.IndexOf(homeRow) < 0) homeRow = rows[0];
            int pos = rows.IndexOf(homeRow);
            List<Movie> l = HomeList(homeRow);
            if (k == Keys.Up)
            {
                if (pos > 0) homeRow = rows[pos - 1]; else navFocus = true;
            }
            else if (k == Keys.Down) { if (pos < rows.Count - 1) homeRow = rows[pos + 1]; }
            else if (k == Keys.Left) homeCol[homeRow] = Math.Max(0, homeCol[homeRow] - 1);
            else if (k == Keys.Right) homeCol[homeRow] = Math.Min(Math.Max(0, l.Count - 1), homeCol[homeRow] + 1);
            else if (k == Keys.Enter && l.Count > 0)
            {
                Movie m = l[Math.Min(homeCol[homeRow], l.Count - 1)];
#if PLAYER
                if (m.Type == "show") { showFrom = Screen2.Home; OpenShow(m); return true; }   // a season added: its show
#endif
                OpenDetails(m, Screen2.Home);
            }
            else return false;
            return true;
        }

        const int Cols = 7;

        // 2.68.0: the grid shows the films or the TV shows.
        List<Movie> GL() { return scr == Screen2.Shows ? shows : movies; }
        int GIdx { get { return scr == Screen2.Shows ? showIdx : gridIdx; } set { if (scr == Screen2.Shows) showIdx = value; else gridIdx = value; } }
        int GScroll { get { return scr == Screen2.Shows ? showScroll : gridScroll; } set { if (scr == Screen2.Shows) showScroll = value; else gridScroll = value; } }

        bool GridKey(Keys k)
        {
            List<Movie> movies = GL();
            int gridIdx = GIdx;
            bool r = GridKey2(k, movies, ref gridIdx);
            GIdx = gridIdx;
            return r;
        }

        bool GridKey2(Keys k, List<Movie> movies, ref int gridIdx)
        {
            int n = movies.Count;
            if (k == Keys.Up) { if (gridIdx < Cols) navFocus = true; else gridIdx -= Cols; }
            else if (k == Keys.Down) { if (gridIdx + Cols < n) gridIdx += Cols; else if (gridIdx / Cols < (n - 1) / Cols) gridIdx = n - 1; }
            else if (k == Keys.Left) gridIdx = Math.Max(0, gridIdx - 1);
            else if (k == Keys.Right) gridIdx = Math.Min(Math.Max(0, n - 1), gridIdx + 1);
            else if (k == Keys.PageDown) gridIdx = Math.Min(Math.Max(0, n - 1), gridIdx + Cols * 2);
            else if (k == Keys.PageUp) gridIdx = Math.Max(0, gridIdx - Cols * 2);
            else if (k == Keys.Enter && n > 0)
            {
#if PLAYER
                if (scr == Screen2.Shows) { showFrom = Screen2.Shows; OpenShow(movies[gridIdx]); return true; }
#endif
                OpenDetails(movies[gridIdx], Screen2.Movies);
            }
            else if (k >= Keys.A && k <= Keys.Z || k >= Keys.D0 && k <= Keys.D9)
            {
                char c = k >= Keys.A && k <= Keys.Z ? (char)('A' + (k - Keys.A)) : (char)('0' + (k - Keys.D0));
                for (int i = 0; i < n; i++)
                {
                    string t = SortTitle(movies[i].Title);
                    if (t.Length > 0 && char.ToUpperInvariant(t[0]) >= c) { gridIdx = i; break; }
                }
            }
            else return false;
            return true;
        }

        // ---- details -----------------------------------------------------------------
        bool detOnVersion;   // 2.55.0: focus on the Version row (films with more than one version)
        void OpenDetails(Movie m, Screen2 from)
        {
            det = m;
            detailsFrom = from;
            detBtn = 0;
            detOnVersion = false;
            scr = Screen2.Details;
            if (!m.Detailed)
            {
                Bg2(delegate
                {
                    object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + m.Key), m.Srv.Token, 15000)), "MediaContainer");
                    object[] md = Plex.Arr(mc, "Metadata");
                    if (md.Length > 0) { Plex.FillMedia(m, md[0]); if (m.Summary.Length == 0) m.Summary = Plex.Str(md[0], "summary"); m.Offset = Plex.Num(md[0], "viewOffset"); }
                }, delegate { Invalidate(); });
            }
        }

        List<string> DetailButtons()
        {
            List<string> b = new List<string>();
            if (det.Offset > 60000) { b.Add("Resume from " + Clock(det.Offset)); b.Add("Play from start"); }
            else b.Add("Play");
            b.Add(det.ViewCount > 0 ? "Mark unwatched" : "Mark watched");
            return b;
        }

        bool DetailsKey(Keys k, bool back)
        {
            List<string> b = DetailButtons();
#if PLAYER
            if (nextAt != DateTime.MaxValue)
            {
                // 2.68.0: the next episode's countdown: Enter plays it now, Back stops.
                if (k == Keys.Enter) { nextAt = DateTime.MaxValue; Play(det, 0); return true; }
                if (back) { nextAt = DateTime.MaxValue; Invalidate(); return true; }
            }
            if (back && detailsFrom == Screen2.Show) { scr = Screen2.Show; LoadEpisodes(); return true; }
#endif
            if (back) { scr = detailsFrom; return true; }
            bool many = det.Versions.Count > 1;
            if (k == Keys.Down && many) { detOnVersion = true; return true; }
            if (k == Keys.Up && detOnVersion) { detOnVersion = false; return true; }
            if (detOnVersion)
            {
                if (k == Keys.Left || k == Keys.Right)
                {
                    int n = det.Versions.Count;
                    det.VerIdx = (det.VerIdx + (k == Keys.Right ? 1 : n - 1)) % n;
                    Plex.Use(det);
                }
                else if (k == Keys.Enter) detOnVersion = false;
                return true;
            }
            if (k == Keys.Left) detBtn = Math.Max(0, detBtn - 1);
            else if (k == Keys.Right) detBtn = Math.Min(b.Count - 1, detBtn + 1);
            else if (k == Keys.Enter)
            {
                string s = b[Math.Min(detBtn, b.Count - 1)];
                if (s.StartsWith("Resume", StringComparison.Ordinal)) Play(det, det.Offset);
                else if (s.StartsWith("Play", StringComparison.Ordinal)) Play(det, 0);
                else
                {
                    Movie m = det;
                    bool watch = m.ViewCount == 0;
                    Bg2(delegate
                    {
                        Plex.Http("GET", Url(m.Srv, "/:/" + (watch ? "scrobble" : "unscrobble") + "?identifier=com.plexapp.plugins.library&key=" + m.Key), m.Srv.Token, 10000);
                    }, delegate { m.ViewCount = watch ? 1 : 0; if (watch) m.Offset = 0; RefreshContinue(); Invalidate(); });
                }
            }
            else return false;
            return true;
        }

        static string Clock(long ms)
        {
            long s = ms / 1000;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60);
        }

        // ---- settings ------------------------------------------------------------------
        int LookAheadMs() { return (int)Math.Round(host.LookAheadFrames() * 1000.0 / 23.976); }
        int TotalDelay() { return LookAheadMs() + unpluggedDelay; }

#if PLAYER
        // 2.69.0: the capture card's HDMI audio. DeckLinkSource plays it on this PC's sound
        // device only while this block says so (Local\ClipGuard_CaptureAudio, "CGA1"), held back
        // by the picture's look-ahead plus the Game audio delay; Dolby / DTS bitstream is muted.
        static System.IO.MemoryMappedFiles.MemoryMappedFile capAudioMap;
        static System.IO.MemoryMappedFiles.MemoryMappedViewAccessor capAudioView;
        int GameAudioDelay()
        {
            int d;
            if (!int.TryParse(Ini("GameAudioDelay", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) d = 0;
            return Math.Max(-500, Math.Min(500, d));
        }
        void PublishCaptureAudio()
        {
            try
            {
                if (capAudioView == null)
                {
                    capAudioMap = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(@"Local\ClipGuard_CaptureAudio", 64);
                    capAudioView = capAudioMap.CreateViewAccessor(0, 64);
                }
                capAudioView.Write(4, 1);                          // version
                capAudioView.Write(8, 1);                          // enabled
                capAudioView.Write(12, host.LookAheadFrames());    // the Video Processor's look-ahead while capturing
                capAudioView.Write(16, GameAudioDelay());
                capAudioView.Write(0, 0x31414743);                 // "CGA1"
            }
            catch (Exception) { }
        }
#endif

        string[] SettingsRows()
        {
            int fr = host.LookAheadFrames();
            string srv = "All servers (" + servers.Count.ToString(CultureInfo.InvariantCulture) + ")";
            Server one = servers.Find(delegate(Server x) { return x.Id == choice; });
            if (one != null) srv = one.Name + (one.Owned ? "" : " (shared with you)");
            return new[] {
#if PLAYER
                // 2.71.0: one row, the delay you get (the look-ahead is inside it and followed).
                "Audio delay\t" + TotalDelay().ToString(CultureInfo.InvariantCulture) + " ms   (Left / Right; [ and ] while a film plays)",
#else
                "Unplugged delay\t" + unpluggedDelay.ToString(CultureInfo.InvariantCulture) + " ms   (Left / Right)",
                "Audio delay now\t" + TotalDelay().ToString(CultureInfo.InvariantCulture) + " ms   (" + (fr >= 12 ? "High Performance" : fr.ToString(CultureInfo.InvariantCulture) + " frames") + " look-ahead + " + unpluggedDelay.ToString(CultureInfo.InvariantCulture) + " ms)",
#endif
                "Lip sync test\tPress Enter",
                "Plex server\t" + srv + "   (Left / Right: choose, Enter: look again)",
                "Sign out of Plex\tPress Enter",
                "Learn remote\tPress Enter   (" + LearnedCount().ToString(CultureInfo.InvariantCulture) + " of " + Remote.Actions.Length.ToString(CultureInfo.InvariantCulture) + " buttons learned)",
#if PLAYER
                "Next episode\t" + (Ini("NextEpisode", "1") != "0" ? "On" : "Off") + "   (plays the next episode after a 10 s countdown; Left / Right)",
                "Game audio delay\t" + GameAudioDelay().ToString(CultureInfo.InvariantCulture) + " ms   (capture card sound, added to the look-ahead; Left / Right)",
#endif
#if PLAYER
                "Close the player\tPress Enter" };
#else
                "Back to Video Processor\tPress Enter   (F4)" };
#endif
        }

        // 2.71.0: the row's meaning: the player has no "Audio delay now" row (row 1), so the
        // rows after it are one place higher on screen than their number here.
#if PLAYER
        int SetRow() { return setIdx >= 1 ? setIdx + 1 : setIdx; }
#else
        int SetRow() { return setIdx; }
#endif

        int LearnedCount()
        {
            int n = 0;
            if (remote != null) foreach (string c in remote) if (!string.IsNullOrEmpty(c)) n++;
            return n;
        }

        bool SettingsKey(Keys k)
        {
            int n = SettingsRows().Length;
            if (k == Keys.Up) { if (setIdx == 0) navFocus = true; else setIdx--; }
            else if (k == Keys.Down) setIdx = Math.Min(n - 1, setIdx + 1);
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 0)
            {
                unpluggedDelay = ClampDelay(unpluggedDelay + (k == Keys.Right ? 5 : -5));
                SetIni(DelayKey, unpluggedDelay.ToString(CultureInfo.InvariantCulture));
            }
#if PLAYER
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 6) SetIni("NextEpisode", Ini("NextEpisode", "1") != "0" ? "0" : "1");
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 7)
            {
                SetIni("GameAudioDelay", Math.Max(-500, Math.Min(500, GameAudioDelay() + (k == Keys.Right ? 10 : -10))).ToString(CultureInfo.InvariantCulture));
                PublishCaptureAudio();
            }
#endif
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 3 && servers.Count > 0)
            {
                // All servers, then each one; the library reloads for the choice.
                int i = choice == "all" ? -1 : servers.FindIndex(delegate(Server x) { return x.Id == choice; });
                int n2 = servers.Count + 1;
                i = ((i + 1) + (k == Keys.Right ? 1 : n2 - 1)) % n2 - 1;
                choice = i < 0 ? "all" : servers[i].Id;
                SetIni("PlexServerChoice", choice);
                LoadLibrary();
            }
            else if (k == Keys.Enter)
            {
                if (SetRow() == 2) StartLipSync();
                else if (SetRow() == 3) FindServers();
                else if (SetRow() == 4)
                {
                    token = ""; servers.Clear(); choice = "all";
                    foreach (string key in new[] { "PlexToken", "PlexServerChoice" }) SetIni(key, "");
                    movies.Clear(); cont.Clear(); recent.Clear();
                    BeginSignIn();
                }
                else if (SetRow() == 5) StartLearn();
#if PLAYER
                else if (SetRow() == 6) SetIni("NextEpisode", Ini("NextEpisode", "1") != "0" ? "0" : "1");
                else if (SetRow() == 8) ExitToProcessor();
#else
                else if (SetRow() == 6) ExitToProcessor();
#endif
            }
            else return false;
            return true;
        }


        // ---- Unplugged ---------------------------------------------------------------
        // 2.52.2: the Unplugged on the HTPC is the new one (not the MPC-HC one): it keeps its
        // mode in %USERPROFILE%\.unplugged\app_config.json and, in Video Processor mode,
        // opens the capture card at start and ignores the film. So: Media Player mode while
        // a film plays, the saved mode back on the way out. (Its audio delay, resume and
        // position are not reachable from outside yet.)
        static string UConfig { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unplugged", "app_config.json"); } }
        static readonly Regex ModeRx = new Regex("(\"app_mode\"\\s*:\\s*\")([^\"]*)(\")");
        string savedMode;

        static string UMode()
        {
            try { Match m = ModeRx.Match(File.ReadAllText(UConfig, Encoding.UTF8)); return m.Success ? m.Groups[2].Value : null; }
            catch (Exception) { return null; }
        }

        static void USetMode(string mode)
        {
            string j = File.ReadAllText(UConfig, Encoding.UTF8);
            if (!ModeRx.IsMatch(j)) return;
            j = ModeRx.Replace(j, delegate(Match m) { return m.Groups[1].Value + mode + m.Groups[3].Value; }, 1);
            File.WriteAllText(UConfig, j, new UTF8Encoding(false));
        }

        void SetUnplugged(int delayMs)
        {
            string cur = UMode();
            if (savedMode == null && cur != null) savedMode = cur;
            if (cur != null && cur != "media_player") USetMode("media_player");
        }

        void RestoreUnplugged()
        {
            if (savedMode == null) return;
            try { if (UMode() != savedMode) USetMode(savedMode); } catch (Exception) { }
            savedMode = null;
        }

        static void CloseUnplugged()
        {
            Process[] ps = Process.GetProcessesByName("Unplugged");
            foreach (Process p in ps) { try { p.CloseMainWindow(); } catch (Exception) { } }
            DateTime until = DateTime.Now.AddSeconds(10);
            foreach (Process p in ps)
            {
                try
                {
                    int left = (int)Math.Max(0, (until - DateTime.Now).TotalMilliseconds);
                    if (!p.WaitForExit(left)) { p.Kill(); p.WaitForExit(5000); }
                }
                catch (Exception) { }
            }
        }

        static string WebGet(string path, int timeoutMs)
        {
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + WebPort.ToString(CultureInfo.InvariantCulture) + path);
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            rq.Proxy = null;
            using (HttpWebResponse r = (HttpWebResponse)rq.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static long Var(string html, string id)
        {
            Match m = Regex.Match(html, "id=\"" + id + "\">(-?\\d+)<");
            long v;
            return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }

        // ---- playing -----------------------------------------------------------------
        void Play(Movie m, long startMs)
        {
#if PLAYER
            BeginStarting(m);   // 2.70.0: the starting screen (the server's drive may be asleep)
            int seq = startSeq;
#else
            Busy("Asking " + m.Srv.Name + " for \"" + m.Title + "\"...");
#endif
            string url = "";
            Bg2(delegate
            {
#if PLAYER
                // 2.57.0: always asked again: the chosen sound / subtitle tracks, chapters, logo.
                try
                {
                    object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + m.Key + "?includeChapters=1"), m.Srv.Token, 15000)), "MediaContainer");
                    object[] md = Plex.Arr(mc, "Metadata");
                    if (md.Length > 0) { Plex.FillMedia(m, md[0]); Plex.FillExtras(m, md[0]); }
                }
                catch (Exception) { if (m.PartKey.Length == 0) throw; }
#else
                if (m.PartKey.Length == 0)
                {
                    object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + m.Key), m.Srv.Token, 15000)), "MediaContainer");
                    object[] md = Plex.Arr(mc, "Metadata");
                    if (md.Length > 0) Plex.FillMedia(m, md[0]);
                }
#endif
                if (m.PartKey.Length == 0) throw new Exception("Plex did not give a file for \"" + m.Title + "\".");
                // Direct play: the original file, as it is on the server's disk.
                url = m.Srv.Uri + m.PartKey + (m.PartKey.Contains("?") ? "&" : "?") + "X-Plex-Token=" + Uri.EscapeDataString(m.Srv.Token);
#if PLAYER
                WakeFile(url, m, seq);   // 2.70.0: the first data arrives before the player opens it
#endif
            },
#if PLAYER
            delegate { if (seq != startSeq) return; EndStarting(); Launch(url, startMs, m, false); },
            delegate(Exception ex) { if (seq != startSeq) return; EndStarting(); scr = startFrom; message = ex.Message; Invalidate(); });
#else
            delegate { Launch(url, startMs, m, false); }, delegate(Exception ex) { scr = Screen2.Details; message = ex.Message; Invalidate(); });
#endif
        }

#if PLAYER
        // ---- 2.70.0: starting a film. A Plex server's drive that has spun down takes up to ~30 s
        // to send the first data; the player opened the file straight away and waited inside the
        // open, frozen. Now the first MB is read here first (the drive wakes), with a starting
        // screen that can be cancelled, and the open that follows is quick.
        Movie startM;
        int startSeq;
        volatile int startStage;            // 0 asking Plex, 1 reading the file
        DateTime startAt = DateTime.Now, stageAt = DateTime.Now;
        HttpWebRequest startReq;
        System.Windows.Forms.Timer startTimer;
        Screen2 startFrom = Screen2.Details;

        void BeginStarting(Movie m)
        {
            startM = m;
            startSeq++;
            startStage = 0;
            startAt = stageAt = DateTime.Now;
            if (scr != Screen2.Starting && scr != Screen2.Busy) startFrom = scr;
            scr = Screen2.Starting;
            if (startTimer == null)
            {
                startTimer = new System.Windows.Forms.Timer();
                startTimer.Interval = 33;
                startTimer.Tick += delegate { if (scr == Screen2.Starting) Invalidate(); else startTimer.Stop(); };
            }
            startTimer.Start();
            Invalidate();
        }

        void EndStarting()
        {
            if (startTimer != null) startTimer.Stop();
            if (scr == Screen2.Starting) scr = startFrom;
        }

        void CancelStarting()
        {
            startSeq++;
            HttpWebRequest r = startReq;
            if (r != null) try { r.Abort(); } catch (Exception) { }
            EndStarting();
            Invalidate();
        }

        void WakeFile(string url, Movie m, int seq)
        {
            stageAt = DateTime.Now;
            startStage = 1;
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = "GET";
            rq.AddRange(0L, 1048575L);
            rq.Timeout = 60000;
            rq.ReadWriteTimeout = 60000;
            rq.KeepAlive = false;
            startReq = rq;
            try
            {
                using (WebResponse r = rq.GetResponse())
                using (Stream st = r.GetResponseStream())
                {
                    byte[] b = new byte[65536];
                    int tot = 0, n;
                    while (tot < 1048576 && seq == startSeq && (n = st.Read(b, 0, b.Length)) > 0) tot += n;
                }
            }
            catch (WebException ex)
            {
                if (seq != startSeq) return;   // cancelled
                if (ex.Status == WebExceptionStatus.Timeout)
                    throw new Exception("The Plex server did not start sending \"" + m.Title + "\" within 60 s. Its drive may still be waking up: try again in a moment.");
                throw;
            }
            finally { startReq = null; }
        }

        void DrawStarting(Graphics g)
        {
            Movie m = startM;
            if (m == null) return;
            Image art = Img(m, m.Art, 1920, 1080);
            if (art != null)
            {
                g.DrawImage(art, new RectangleF(0, 0, 1920, 1080));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(215, Bg))) g.FillRectangle(b, 0, 0, 1920, 1080);
            }
            Poster(g, m, 0, 200, 270, 360, 540, false);
            const float x = 640, w = 1080;
            bool ep = m.Type == "episode";
            TextBox(g, ep ? m.ShowTitle : m.Title, 56, true, Color.White, new RectangleF(x, 300, w, 80));
            List<string> meta = new List<string>();
            if (ep) meta.Add(m.Code + "  " + m.Title);
            else if (m.Year > 0) meta.Add(m.Year.ToString(CultureInfo.InvariantCulture));
            if (m.Duration > 0) meta.Add(Left(m.Duration));
            Txt(g, string.Join("  \u00b7  ", meta.ToArray()), 26, false, Grey, x, 392);

            DateTime now = DateTime.Now;
            double se = (now - stageAt).TotalSeconds;
            string srv = m.Srv != null ? m.Srv.Name : "Plex";
            string line = startStage == 0 ? "Asking " + srv + "..." : (se < 2 ? "Opening the file..." : "Waking the server's drive...");
            Txt(g, line, 32, true, Color.White, x, 500);

            // A yellow light gliding along a track, easing at the ends.
            RectangleF track = new RectangleF(x, 566, w, 8);
            using (SolidBrush b = new SolidBrush(Dim)) g.FillRectangle(b, track);
            double t = (now.Ticks / 10000L % 1800L) / 1800.0;
            double p = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t);
            const float seg = 240;
            float sx = x + (float)(p * (w - seg));
            using (LinearGradientBrush lb = new LinearGradientBrush(new RectangleF(sx - 1, 0, seg + 2, 1), Color.FromArgb(0, Yellow), Yellow, LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = new[] { Color.FromArgb(0, Yellow), Yellow, Color.FromArgb(0, Yellow) };
                cb.Positions = new[] { 0f, 0.5f, 1f };
                lb.InterpolationColors = cb;
                g.FillRectangle(lb, sx, track.Y - 2, seg, track.Height + 4);
            }

            if (startStage == 1 && se >= 2)
                Txt(g, ((int)se).ToString(CultureInfo.InvariantCulture) + " s   \u00b7   a drive that has spun down takes up to 30 s", 22, false, Grey, x, 600);
            Txt(g, "Back: cancel", 20, false, Grey, x, 680);
        }
#endif

        void StartLipSync()
        {
            string clip = Path.Combine(appDir, "lipsync.mkv");   // 2.63.2: the 4K 23.976 Dolby Digital test with the ms scale
            if (!File.Exists(clip)) { message = "lipsync.mkv is missing next to ClipGuard.exe."; Invalidate(); return; }
            lipDelta = 0;
            Launch(clip, 0, null, true);
        }

#if !PLAYER
        void Launch(string file, long startMs, Movie m, bool lip)
        {
            Busy(lip ? "Starting the lip sync test..." : "Starting \"" + m.Title + "\"...");
            int delay = TotalDelay();
            Process p = null;
            Bg2(delegate
            {
                CloseUnplugged();
                SetUnplugged(delay);
                string args = "\"" + file + "\"";   // 2.52.2: the new Unplugged takes only the file
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(workRoot, "Unplugged.exe"), args);
                psi.WorkingDirectory = workRoot;
                psi.UseShellExecute = false;
                p = Process.Start(psi);
                for (int i = 0; i < 150 && !p.HasExited; i++)
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero) break;
                    Thread.Sleep(100);
                }
            }, delegate
            {
                player = p;
                playing = m;
                lipSync = lip;
                lastPos = startMs > 0 ? startMs : -1;
                lastDur = m != null ? m.Duration : 0;
                lastState = -1;
                nextTimeline = DateTime.Now.AddSeconds(10);
                Hide();
                try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(p.MainWindowHandle); } catch (Exception) { }
                RegisterHotKey(Handle, HK_F4, MOD_NOREPEAT, (uint)Keys.F4);
                if (lip)
                {
                    RegisterHotKey(Handle, HK_L, 0, (uint)Keys.Left);
                    RegisterHotKey(Handle, HK_R, 0, (uint)Keys.Right);
                    RegisterHotKey(Handle, HK_ENTER, MOD_NOREPEAT, (uint)Keys.Enter);
                    RegisterHotKey(Handle, HK_ESC, MOD_NOREPEAT, (uint)Keys.Escape);
                }
                playTimer.Start();
            });
        }

        protected override void WndProc(ref Message msg)
        {
            if (msg.Msg == WM_HOTKEY && player != null)
            {
                int id = msg.WParam.ToInt32();
                if (id == HK_F4 || id == HK_ESC) StopFilm(false);
                else if (id == HK_ENTER && lipSync) StopFilm(true);
                else if (id == HK_L || id == HK_R)
                {
                    int cmd = id == HK_R ? 905 : 906;   // ID_PLAY_INCAUDDELAY / DECAUDDELAY: 10 ms
                    lipDelta += id == HK_R ? 10 : -10;
                    ThreadPool.QueueUserWorkItem(delegate { try { WebGet("/command.html?wm_command=" + cmd.ToString(CultureInfo.InvariantCulture), 2000); } catch (Exception) { } });
                }
                return;
            }
            if (msg.Msg == 0x0319)   // WM_APPCOMMAND: learning, or a learned remote button
            {
                int cmd = (int)(((long)msg.LParam >> 16) & 0x0FFF);
                if (learning) { LearnCode(Keys.Attn, Remote.OfApp(cmd)); Invalidate(); msg.Result = (IntPtr)1; return; }
                Keys t = Remote.TranslateApp(remote, cmd);
                if (t != Keys.None && OnKey(t)) { Invalidate(); msg.Result = (IntPtr)1; return; }
            }
            base.WndProc(ref msg);
        }

        void PlayTick()
        {
            if (player == null) { playTimer.Stop(); return; }
            bool exited;
            try { exited = player.HasExited; } catch (Exception) { exited = true; }
            if (exited) { FilmEnded(); return; }
            if (polling) return;
            polling = true;
            Movie m = playing;
            bool sendTl = m != null && DateTime.Now >= nextTimeline;
            if (sendTl) nextTimeline = DateTime.Now.AddSeconds(10);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string h = WebGet("/variables.html", 1500);
                    long pos = Var(h, "position"), dur = Var(h, "duration");
                    int st = (int)Var(h, "state");
                    if (pos >= 0 && dur > 0) { lastPos = pos; lastDur = dur; }
                    if (st >= 0) lastState = st;
                    if (sendTl && lastPos >= 0) Timeline(m, lastState == 1 ? "paused" : "playing");
                }
                catch (Exception) { }
                polling = false;
            });
        }

#endif
#if PLAYER
        // ---- ClipGuard+ Player (test): the film plays here, in our own engine build -------
        // (s3engine.dll next to the player: Unplugged's engine with Plex streaming added).
        VideoForm video;
        Overlay ovl;
        string curUrl = "";
        Ver curVer;                         // 2.57.0: the version playing (its Plex streams)
        int audioCur = -1, subCur = -1;     // engine track numbers asked for (-1: the file's default / Off)
        int reconnects;
        DateTime lastMove = DateTime.MinValue, lastReconnect = DateTime.MinValue;
        long lastMovePos = -1;
        bool reconnecting, paused;
        Image logoImg;

        // ---- 2.64.0: Video Processor (DeckLink capture) ---------------------------------
        bool capturing;
        string capDevice = "";
        int capLockedLast = -1;
        DateTime capStarted = DateTime.MinValue;

        // The card: Picture > Capture card, else the one Unplugged last used, else the Mini Recorder 4K.
        string CaptureDevice()
        {
            string d = Ini("CaptureDevice", "");
            if (d.Length > 0) return d;
            try
            {
                Match mm = Regex.Match(File.ReadAllText(UConfig, Encoding.UTF8), "\"active_input_url\"\\s*:\\s*\"decklink://([^\"]+)\"");
                if (mm.Success) return mm.Groups[1].Value;
            }
            catch (Exception) { }
            return "DeckLink Mini Recorder 4K";
        }

        void EnsureVideo()
        {
            if (video == null)
            {
                video = new VideoForm(this, UnpluggedScreen().Bounds);
                video.Show();
                ControlForm.PlayerVideoWindow = video.Handle;   // 2.54.0: ClipGuard's keys work on the picture
                Engine.Attach(video.Host.Handle);
                ovl = new Overlay(this);
            }
            else video.Show();
            video.Activate();
        }

        public void StartCapture()
        {
            if (player != null || capturing || quitting) return;   // (also from Start in, while the library still loads)
            capDevice = CaptureDevice();
            capturing = true;
            playing = null;
            lipSync = false;
            menuOpen = false;
            barUntil = DateTime.MinValue; msgUntil = DateTime.MinValue;
            capLockedLast = -1; capUntil = DateTime.MinValue;
            capWaiting = false; capInfo = false; capSig = "";
            capStarted = DateTime.Now;
            pushedGen = -1;
            capFpsLast = 0; lutCompareOff = false;
            if (PHost != null) PHost.SetPlayerSource(2);   // 2.64.1: Video Processor's look-ahead
            PublishCaptureAudio();                          // 2.69.0: before the card starts (the filter reads it then)
            EnsureVideo();
            Hide();
            string dev = capDevice;
            video.BeginInvoke((MethodInvoker)delegate
            {
                UnpluggedSettings.Push();
                int rc = Engine.StartDevice(dev);
                if (rc != 0)
                {
                    capturing = false;
                    player = null;
                    scr = Screen2.VP; tab = TabVP; navFocus = true;
                    message = "Video Processor could not start the capture card \"" + dev + "\" (0x" + rc.ToString("X8") + ").";
                    ShowOverVideo();   // 2.70.0: the library first, then the picture goes (no desktop between)
                    return;
                }
                player = Process.GetCurrentProcess();   // the picture is up
                playTimer.Interval = 1000;
                playTimer.Start();
                CapCard("Video Processor", dev, 5000);
            });
        }

        // 2.64.1: Video Processor's messages: a card in the library's style, bottom centre.
        string capTitle = "", capLine = "";
        DateTime capUntil = DateTime.MinValue;
        void CapCard(string title, string line, int ms)
        {
            capTitle = title; capLine = line;
            capInfo = false;
            capUntil = DateTime.Now.AddMilliseconds(ms);
            Kick();
        }

        // ---- 2.70.0: Waiting for signal, and the Info card ----------------------------------
        bool capWaiting;          // no signal: the waiting card stays up until the picture comes
        bool capInfo;             // the card on screen is Info
        int capWaitTick;
        string capSig = "";       // the signal's format last shown (a change brings Info up again)
        int sLocked, sW, sH, sNum, sDen, sCs, sDr, sDepth, sChroma, sIl;

        void ReadSignal(string j)
        {
            Match g = Regex.Match(j, "\"signal\"\\s*:\\s*\\{([^}]*)\\}");
            if (!g.Success) { sLocked = -1; return; }
            string b = g.Groups[1].Value;
            sLocked = JInt(b, "locked"); sW = JInt(b, "w"); sH = JInt(b, "h"); sNum = JInt(b, "fpsNum"); sDen = JInt(b, "fpsDen");
            sCs = JInt(b, "cs"); sDr = JInt(b, "dr"); sDepth = JInt(b, "depth"); sChroma = JInt(b, "chroma"); sIl = JInt(b, "il");
            // 2.71.0: the signal monitor leaves size and rate at 0; the capture filter has them.
            Match d = Regex.Match(j, "\"dl\"\\s*:\\s*\\{([^}]*)\\}");
            if (d.Success)
            {
                string c = d.Groups[1].Value;
                if (sW <= 0 || sH <= 0) { sW = JInt(c, "w"); sH = JInt(c, "h"); }
                if (sNum <= 0 || sDen <= 0) { sNum = JInt(c, "fpsNum"); sDen = JInt(c, "fpsDen"); }
            }
        }

        static int JInt(string b, string key)
        {
            Match m = Regex.Match(b, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        void ShowInfo(int ms)
        {
            capInfo = true; capWaiting = false;
            capUntil = DateTime.Now.AddMilliseconds(ms);
            Kick();
        }

        string FpsText()
        {
            if (sNum <= 0 || sDen <= 0) return "";
            double f = (double)sNum / sDen;
            string t = Math.Abs(f - Math.Round(f)) < 0.005 ? Math.Round(f).ToString(CultureInfo.InvariantCulture) : f.ToString(f < 30 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
            return t + (sIl != 0 ? "i" : "p");
        }

        string[][] InfoRows()
        {
            List<string[]> r = new List<string[]>();
            string pic = SizeRate(sW, sH, sNum, sDen, sIl);
            if (pic.Length > 0 && Ini("LastSignalFormat", "") != pic) SetIni("LastSignalFormat", pic);   // 2.71.0: for the Video Processor page
            if (sDepth > 0) pic += ", " + sDepth.ToString(CultureInfo.InvariantCulture) + "-bit";
            if (sChroma == 0) pic += " 4:2:2"; else if (sChroma == 1) pic += " RGB 4:4:4";
            r.Add(new[] { "Picture", pic });
            string dr = sDr == 1 ? "HDR10 (PQ)" : (sDr == 2 ? "HLG" : (sDr == 3 ? "Dolby Vision" : "SDR"));
            string cs = sCs == 2 ? "BT.2020" : (sCs == 1 ? "BT.709" : (sCs == 0 && sDr == 0 ? "BT.601" : ""));
            r.Add(new[] { "HDR", dr + (cs.Length > 0 ? ", " + cs : "") + (sDr == 1 || sDr == 2 ? "   (measured every frame)" : "") });
            r.Add(new[] { "Sound", SoundText() });
            r.Add(new[] { "Projector", ProjectorText() });
            int la = host.LookAheadFrames();
            int ms = sNum > 0 && sDen > 0 ? (int)Math.Round(la * 1000.0 * sDen / sNum) : 0;
            r.Add(new[] { "Look-ahead", la <= 0 ? "Off" : (la >= 12 ? "High Performance" : la.ToString(CultureInfo.InvariantCulture) + " frames" + (ms > 0 ? " (" + ms.ToString(CultureInfo.InvariantCulture) + " ms)" : "")) });
            return r.ToArray();
        }

        string SoundText()
        {
            int st = 0, act = 0, dev = 0;
            try
            {
                if (capAudioView != null) { st = capAudioView.ReadInt32(20); act = capAudioView.ReadInt32(24); dev = capAudioView.ReadInt32(28); }
            }
            catch (Exception) { }
            if (st == 2) return "Dolby / DTS bitstream on the card: muted (it plays from the source to the receiver)";
            if (st == 3) return "No sound on the card";
            if (st != 1) return "Not playing here (the source's sound goes to the receiver)";
            string ch = act == 2 ? "2.0" : (act == 6 ? "5.1" : (act == 8 ? "7.1" : act.ToString(CultureInfo.InvariantCulture) + " ch"));
            return "PCM " + ch + " playing" + (dev == 1 ? " (exclusive)" : (dev == 2 ? " (shared)" : "")) + ", held back with the picture";
        }

        string ProjectorText()
        {
            int hz = 0;
            try
            {
                DEVMODE cur = NewMode();
                if (EnumDisplaySettingsW(FrmDevice(), ENUM_CURRENT_SETTINGS, ref cur)) hz = (int)cur.dmDisplayFrequency;
            }
            catch (Exception) { }
            string t = hz > 0 ? HzName(hz) + " Hz" : "";
            if (frmChanged) t += "   (matched to the source)";
            else if (!FrmOn()) t += "   (frame rate matching Off)";
            return t;
        }

        void DrawInfoCard(Graphics g, float W)
        {
            string[][] rows = InfoRows();
            const float w = 1240;
            float h = 150 + rows.Length * 46 + 50;
            float x = (W - w) / 2, y = 1080 - h - 60;
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, x, y, w, h);
            using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, x, y, w, 5);
            Txt(g, "Info", 34, true, Color.White, x + 40, y + 26);
            Txt(g, capDevice, 24, false, Grey, x + 40, y + 80);
            float ry = y + 130;
            foreach (string[] r in rows)
            {
                Txt(g, r[0], 24, true, Grey, x + 40, ry);
                Txt(g, r[1], 24, false, Color.White, x + 230, ry);
                ry += 46;
            }
            Txt(g, "F7  ClipGuard      I  info      Back  library", 20, false, Grey, x + 40, y + h - 44);
        }

        void DrawWaitCard(Graphics g, float W)
        {
            const float w = 900, h = 200;
            float x = (W - w) / 2, y = 1080 - h - 70;
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, x, y, w, h);
            using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, x, y, w, 5);
            Txt(g, "Waiting for signal", 34, true, Color.White, x + 40, y + 28);
            // three dots, one lit in turn (moves each second)
            for (int i = 0; i < 3; i++)
                using (SolidBrush db = new SolidBrush(i == capWaitTick % 3 ? Yellow : Dim)) g.FillEllipse(db, x + 390 + i * 26, y + 46, 14, 14);
            Txt(g, capDevice + ":  turn on or wake the source", 26, false, Color.FromArgb(220, 222, 226), x + 40, y + 86);
            Txt(g, "F7  ClipGuard      Back  library", 20, false, Grey, x + 40, y + 144);
        }

        void DrawCapCard(Graphics g, float W)
        {
            if (capWaiting) { DrawWaitCard(g, W); return; }
            if (capInfo) { DrawInfoCard(g, W); return; }
            const float w = 900, h = 190;
            float x = (W - w) / 2, y = 1080 - h - 70;
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, x, y, w, h);
            using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, x, y, w, 5);
            Txt(g, capTitle, 34, true, Color.White, x + 40, y + 28);
            Txt(g, capLine, 26, false, Color.FromArgb(220, 222, 226), x + 40, y + 84);
            Txt(g, "F7  ClipGuard      I  info      Back  library", 20, false, Grey, x + 40, y + 136);
        }

        void CaptureTick()
        {
            int gen;
            long pos, dur;
            Engine.State(out gen, out pos, out dur);
            // A new graph (the source changed format) is given the picture settings again.
            if (gen >= 1 && gen != pushedGen) { pushedGen = gen; UnpluggedSettings.Push(); }
            int locked = -1;
            try
            {
                ReadSignal(Engine.Stats());
                if (sLocked >= 0) locked = sLocked == 0 ? 0 : 1;
            }
            catch (Exception) { }
            // 2.70.0: Waiting for signal stays up until the picture comes; then Info (and
            // again whenever the source changes format).
            if (locked == 0 && (DateTime.Now - capStarted).TotalSeconds > 3)
            {
                capWaiting = true; capInfo = false; capWaitTick++;
                capUntil = DateTime.Now.AddMilliseconds(2500);
                capSig = "";
                Kick();
            }
            else if (locked == 1)
            {
                string sig = sW + "x" + sH + "@" + sNum + "/" + sDen + " " + sDr + " " + sCs + " " + sDepth + " " + sChroma + " " + sIl;
                if (capWaiting || sig != capSig) { capSig = sig; ShowInfo(8000); }
            }
            if (locked >= 0) capLockedLast = locked;
            FrmCaptureTick();   // 2.65.0: frame rate matching
            PublishCaptureAudio();   // 2.69.0: follows look-ahead changes (F7)
        }

        bool CaptureKey(Keys k, bool ctrl)
        {
            if (ctrl && k == Keys.S) { StopCapture(); return true; }
            if (ctrl) return false;
            switch (k)
            {
                case Keys.Escape: Quit(); return true;
                case Keys.F4: case Keys.Back: case Keys.BrowserBack: case Keys.MediaStop: StopCapture(); return true;
                case Keys.I: if (capWaiting) Kick(); else ShowInfo(8000); return true;   // 2.70.0: Info
            }
            return false;
        }

        public void StopCapture()
        {
            if (!capturing) return;
            playTimer.Stop();
            try { Engine.StartDevice(""); } catch (Exception) { }
            try { Engine.Cmd("{\"a\":\"stopgraph\"}"); } catch (Exception) { }
            capturing = false;
            player = null;
            menuOpen = false;
            barUntil = msgUntil = capUntil = DateTime.MinValue;
            if (PHost != null) PHost.SetPlayerSource(0);
            FrmRestore();
            if (ovl != null) ovl.Hide();
            if (scr != Screen2.Busy && scr != Screen2.SignIn) { scr = Screen2.VP; tab = TabVP; navFocus = true; }
            ShowOverVideo();   // 2.70.0
        }

        // 2.70.0: back to the library with no desktop showing between: the library comes up
        // (painted) over the picture, and only then is the picture window hidden.
        void ShowOverVideo()
        {
            if (!Visible) Show();
            Activate();
            BringToFront();
            Invalidate();
            Refresh();
            if (video != null) video.Hide();
        }

        void DrawVP(Graphics g)
        {
            Txt(g, "Video Processor", 40, true, Color.White, 96, 170);
            Txt(g, "The capture card's picture, full screen, through ClipGuard and the Picture settings.", 26, false, Grey, 96, 240);
            Txt(g, "Card:  " + CaptureDevice(), 28, false, Color.White, 96, 320);
            // 2.71.0: the input as it is now (read from the card without starting the capture).
            VpPoll(false);
            Txt(g, "Signal:  " + vpSigText, 24, false, vpSigOk ? Color.White : Grey, 96, 368);
            RectangleF r = new RectangleF(96, 450, 520, 80);
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, r);
            Txt(g, "Start   (Enter or F4)", 28, true, Color.White, 126, 472);
            if (!navFocus) Focus(g, r);
            Txt(g, "While it runs:  F7 ClipGuard menu,   I  info,   Back / F4  library,   Esc  close the player.", 22, false, Grey, 96, 580);
        }

        // ---- 2.71.0: the card's input on the Video Processor page --------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct VFSignalInfo { public int colorspace, dynamicRange, depth, chroma, locked; }
        [DllImport("decklink_monitor.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int vf_dl_open(string name);
        [DllImport("decklink_monitor.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int vf_dl_get_signal_info(ref VFSignalInfo s);
        [DllImport("decklink_monitor.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int vf_dl_get_input_format(out int w, out int h, out int num, out int den, out int il);

        string vpSigText = "...";
        bool vpSigOk, vpMonOpen, vpMonBroken;
        DateTime vpPolled = DateTime.MinValue;
        System.Windows.Forms.Timer vpTimer;

        void VpPoll(bool force)
        {
            if (vpTimer == null)
            {
                vpTimer = new System.Windows.Forms.Timer();
                vpTimer.Interval = 1000;
                vpTimer.Tick += delegate
                {
                    if (scr != Screen2.VP || capturing || !Visible) { vpTimer.Stop(); return; }
                    VpPoll(true);
                    Invalidate();
                };
            }
            if (!vpTimer.Enabled && !capturing) vpTimer.Start();
            if (!force && (DateTime.Now - vpPolled).TotalMilliseconds < 900) return;
            vpPolled = DateTime.Now;
            if (vpMonBroken || capturing) return;
            try
            {
                VFSignalInfo si = new VFSignalInfo();
                int rc = vpMonOpen ? vf_dl_get_signal_info(ref si) : -1;
                if (rc != 0)
                {
                    vpMonOpen = vf_dl_open(CaptureDevice()) == 0;
                    rc = vpMonOpen ? vf_dl_get_signal_info(ref si) : -1;
                }
                if (rc != 0) { vpSigOk = false; vpSigText = "the card is not answering (is Desktop Video installed?)"; return; }
                if (si.locked == 0) { vpSigOk = false; vpSigText = "none  (turn on or wake the source)"; return; }
                int w = 0, h = 0, num = 0, den = 0, il = 0;
                bool fmt = false;
                try { fmt = vf_dl_get_input_format(out w, out h, out num, out den, out il) == 0 && w > 0; } catch (EntryPointNotFoundException) { }
                string tail = "  \u00b7  " + HdrText(si.dynamicRange, si.colorspace) + "  \u00b7  " + si.depth.ToString(CultureInfo.InvariantCulture) + "-bit" + (si.chroma == 0 ? " 4:2:2" : (si.chroma == 1 ? " RGB 4:4:4" : ""));
                if (fmt) vpSigText = SizeRate(w, h, num, den, il) + tail;
                else
                {
                    string last = Ini("LastSignalFormat", "");
                    vpSigText = (last.Length > 0 ? last + " (last seen)" : "picture present") + tail;
                }
                vpSigOk = true;
            }
            catch (Exception) { vpMonBroken = true; vpSigOk = false; vpSigText = "not available"; }
        }

        static string SizeRate(int w, int h, int num, int den, int il)
        {
            string t = w > 0 ? w.ToString(CultureInfo.InvariantCulture) + " x " + h.ToString(CultureInfo.InvariantCulture) : "";
            if (num > 0 && den > 0)
            {
                double f = (double)num / den;
                string r = Math.Abs(f - Math.Round(f)) < 0.005 ? Math.Round(f).ToString(CultureInfo.InvariantCulture) : f.ToString(f < 30 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
                t += (t.Length > 0 ? ", " : "") + r + (il != 0 ? "i" : "p");
            }
            return t;
        }

        static string HdrText(int dr, int cs)
        {
            string d = dr == 1 ? "HDR10 (PQ)" : (dr == 2 ? "HLG" : (dr == 3 ? "Dolby Vision" : "SDR"));
            string c = cs == 2 ? "BT.2020" : (cs == 1 ? "BT.709" : (cs == 0 && dr == 0 ? "BT.601" : ""));
            return d + (c.Length > 0 ? ", " + c : "");
        }

        // ---- 2.65.0: Picture: the player's own settings, in sections ----------------------
        int picIdx;            // the row in the open section, or the section in the list
        int picSec = -1;       // -1: the list of sections

        // The sections shown: Curve Settings only when the tone curve has settings (GT7 Adapt).
        static List<int> PicSections()
        {
            List<int> l = new List<int>();
            for (int i = 0; i < UnpluggedSettings.Sections.Length; i++)
                if (i != 1 || UnpluggedSettings.SectionRows(i).Count > 0) l.Add(i);
            return l;
        }

        bool PictureKey(Keys k)
        {
            if (picSec < 0)
            {
                List<int> secs = PicSections();
                int n = secs.Count;
                if (k == Keys.Up) { if (picIdx == 0) navFocus = true; else picIdx--; }
                else if (k == Keys.Down) picIdx = Math.Min(n - 1, picIdx + 1);
                else if (k == Keys.Enter || k == Keys.Right) { picSec = secs[Math.Max(0, Math.Min(n - 1, picIdx))]; picIdx = 0; }
                else return false;
                Invalidate();
                return true;
            }
            List<int> rows = UnpluggedSettings.SectionRows(picSec);
            if (k == Keys.Up) { if (picIdx == 0) { picIdx = Math.Max(0, PicSections().IndexOf(picSec)); picSec = -1; } else picIdx--; }
            else if (k == Keys.Down) picIdx = Math.Min(Math.Max(0, rows.Count - 1), picIdx + 1);
            else if ((k == Keys.Left || k == Keys.Right || k == Keys.Enter) && picIdx < rows.Count)
            {
                int dir = k == Keys.Left ? -1 : 1;
                string key = UnpluggedSettings.PRows[rows[picIdx]].Key;
                if (key == "!startin") SetIni("StartIn", Ini("StartIn", "Library") == "VideoProcessor" ? "Library" : "VideoProcessor");
                else if (key == "!frm") SetIni("MatchFrameRate", FrmOn() ? "0" : "1");
                else if (key == "!calrun") { if (k == Keys.Enter) CalBegin(); }
                else if (key == "!lut") LutStep(dir);
                else if (key == "!caldir") { }
                else if (key == "!copy")
                {
                    if (k == Keys.Enter)
                        message = UnpluggedSettings.ImportFromUnplugged()
                            ? "Picture settings copied again from Unplugged."
                            : "Unplugged's settings were not found, so nothing was copied.";
                }
                else UnpluggedSettings.PictureStep(rows[picIdx], dir);
            }
            else return false;
            Invalidate();
            return true;
        }

        // Back in a section goes to the list of sections.
        bool PictureBack()
        {
            if (scr != Screen2.Picture || picSec < 0 || navFocus) return false;
            picIdx = Math.Max(0, PicSections().IndexOf(picSec)); picSec = -1;
            Invalidate();
            return true;
        }

        string PlayerRowValue(string key)
        {
            if (key == "!startin") return Ini("StartIn", "Library") == "VideoProcessor" ? "Video Processor" : "Library";
            if (key == "!frm") return FrmOn() ? "On" : "Off";
            if (key == "!copy") return "Press Enter";
            if (key == "!calrun") return "Press Enter";
            if (key == "!lut") return LutName(UnpluggedSettings.LutFile());
            if (key == "!caldir") return CalDir;
            return "";
        }

        void DrawPicture(Graphics g)
        {
            const float rowH = 50;
            int vis = 13;
            if (picSec < 0)
            {
                List<int> secs = PicSections();
                Txt(g, "Picture", 26, true, Color.White, 126, 128);
                for (int i = 0; i < secs.Count; i++)
                {
                    float y = 180 + i * (rowH + 6);
                    RectangleF rr = new RectangleF(96, y, 1728, rowH);
                    using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, rr);
                    Txt(g, UnpluggedSettings.Sections[secs[i]], 22, true, Color.White, 126, y + 11);
                    Txt(g, UnpluggedSettings.SectionHelpOf(secs[i]), 20, false, i == picIdx ? Color.White : Grey, 640, y + 13);
                    if (!navFocus && i == picIdx) Focus(g, rr);
                }
                TextBox(g, "Enter opens a section; Back returns here. Changes are used when a film or Video Processor next starts.", 20, false, Grey, new RectangleF(96, 924, 1728, 90));
                return;
            }
            List<int> rows = UnpluggedSettings.SectionRows(picSec);
            Txt(g, "Picture  ›  " + UnpluggedSettings.Sections[picSec], 26, true, Color.White, 126, 128);
            if (rows.Count == 0)
            {
                Txt(g, "The Unplugged tone curve has no settings of its own (GT7 Adapt has).", 22, false, Grey, 126, 190);
                return;
            }
            int first = Math.Max(0, Math.Min(picIdx - vis / 2, rows.Count - vis));
            for (int r = 0; r < vis && first + r < rows.Count; r++)
            {
                int i = first + r;
                UnpluggedSettings.PRow p = UnpluggedSettings.PRows[rows[i]];
                string val = p.Key.StartsWith("!") ? PlayerRowValue(p.Key) : UnpluggedSettings.ValueOf(rows[i]);
                float y = 180 + r * (rowH + 6);
                RectangleF rr = new RectangleF(96, y, 1728, rowH);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, rr);
                Txt(g, p.Label, 22, true, Color.White, 126, y + 11);
                Txt(g, val, 22, false, i == picIdx ? Color.White : Grey, 820, y + 11);
                if (!navFocus && i == picIdx) Focus(g, rr);
            }
            string help = picIdx < rows.Count ? UnpluggedSettings.PRows[rows[picIdx]].Help : "";
            TextBox(g, help + "   Left / Right changes it; Back: sections.", 20, false, Grey, new RectangleF(96, 924, 1728, 90));
        }

        // ---- 2.65.0: frame rate matching -----------------------------------------------------
        // The graphics card's refresh rate follows the film or the capture source (resolution
        // unchanged), as a temporary change (CDS_FULLSCREEN): back to the desktop rate in the
        // library, and Windows' own setting is never written.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);
        const int ENUM_CURRENT_SETTINGS = -1, CDS_FULLSCREEN = 4, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

        bool frmChanged;       // the rate was changed by us (restore it in the library)
        int frmHz;             // the rate we set
        bool frmDone;          // a film: matched once
        DateTime frmResumeAt = DateTime.MaxValue;
        long frmResumePos = -1;
        double capFpsLast;

        bool FrmOn() { return Ini("MatchFrameRate", "1") != "0"; }
        string FrmDevice() { return UnpluggedScreen().DeviceName; }

        static DEVMODE NewMode() { DEVMODE d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }

        // Windows' whole-number rates: 23 = 23.976, 59 = 59.94.
        static int[] FrmTargets(double fps)
        {
            if (Math.Abs(fps - 23.976) < 0.02) return new[] { 23, 24 };
            if (Math.Abs(fps - 24.0) < 0.02) return new[] { 24, 23 };
            if (Math.Abs(fps - 25.0) < 0.05) return new[] { 50, 25 };
            if (Math.Abs(fps - 29.97) < 0.05) return new[] { 59, 60 };
            if (Math.Abs(fps - 30.0) < 0.05) return new[] { 60, 59 };
            if (Math.Abs(fps - 50.0) < 0.1) return new[] { 50 };
            if (Math.Abs(fps - 59.94) < 0.05) return new[] { 59, 60 };
            if (Math.Abs(fps - 60.0) < 0.05) return new[] { 60, 59 };
            return new int[0];
        }

        // True when the rate was changed (the projector and XMC-2 re-sync).
        bool FrmApply(double fps)
        {
            if (!FrmOn() || fps <= 1) return false;
            int[] want = FrmTargets(fps);
            if (want.Length == 0) return false;
            string dev = FrmDevice();
            DEVMODE cur = NewMode();
            if (!EnumDisplaySettingsW(dev, ENUM_CURRENT_SETTINGS, ref cur)) return false;
            if (cur.dmDisplayFrequency == want[0]) return false;
            foreach (int hz in want)
            {
                if (hz == cur.dmDisplayFrequency) return false;   // already a good match
                for (int i = 0; ; i++)
                {
                    DEVMODE m = NewMode();
                    if (!EnumDisplaySettingsW(dev, i, ref m)) break;
                    if (m.dmPelsWidth != cur.dmPelsWidth || m.dmPelsHeight != cur.dmPelsHeight || m.dmDisplayFrequency != hz) continue;
                    if (m.dmBitsPerPel != cur.dmBitsPerPel) continue;
                    DEVMODE set = cur;
                    set.dmDisplayFrequency = hz;
                    set.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                    int rc = ChangeDisplaySettingsExW(dev, ref set, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
                    if (rc == 0) { frmChanged = true; frmHz = hz; return true; }
                    return false;
                }
            }
            return false;
        }

        void FrmRestore()
        {
            if (!frmChanged) return;
            frmChanged = false; frmHz = 0;
            try { ChangeDisplaySettingsExW(FrmDevice(), IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); } catch (Exception) { }
        }

        static string HzName(int hz) { return hz == 23 ? "23.976" : (hz == 59 ? "59.94" : hz.ToString(CultureInfo.InvariantCulture)); }

        // The film's or the source's frame rate, as the engine has it (0 = not known yet).
        static double EngineFps(bool capture)
        {
            string j = Engine.Stats();
            if (capture)
            {
                Match sg = Regex.Match(j, "\"signal\"\\s*:\\s*\\{\\s*\"locked\"\\s*:\\s*1\\s*,\\s*\"w\"\\s*:\\s*\\d+\\s*,\\s*\"h\"\\s*:\\s*\\d+\\s*,\\s*\"fpsNum\"\\s*:\\s*(\\d+)\\s*,\\s*\"fpsDen\"\\s*:\\s*(\\d+)");
                if (sg.Success)
                {
                    double n = double.Parse(sg.Groups[1].Value, CultureInfo.InvariantCulture), d = double.Parse(sg.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (n > 0 && d > 0) return n / d;
                }
            }
            Match md = Regex.Match(j, "\"media\"\\s*:\\s*\\{\\s*\"valid\"\\s*:\\s*1[^}]*\"fps\"\\s*:\\s*([0-9.]+)");
            return md.Success ? double.Parse(md.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // A film: match once, with the film held while the projector and XMC-2 re-sync.
        void FrmFilmTick(string st)
        {
            if (frmResumeAt != DateTime.MaxValue)
            {
                if (DateTime.Now >= frmResumeAt)
                {
                    frmResumeAt = DateTime.MaxValue;
                    if (frmResumePos >= 0) Engine.Cmd("{\"a\":\"seek\",\"pos\":" + (frmResumePos * 10000L).ToString(CultureInfo.InvariantCulture) + "}");
                    Engine.Cmd("{\"a\":\"play\"}");
                }
                return;
            }
            if (frmDone || (st != "playing" && st != "paused")) return;
            double fps = 0;
            try { fps = EngineFps(false); } catch (Exception) { }
            if (fps <= 1) return;
            frmDone = true;
            long at = Math.Max(0, lastPos);
            if (FrmApply(fps))
            {
                Engine.Cmd("{\"a\":\"pause\"}");
                frmResumePos = at;
                frmResumeAt = DateTime.Now.AddSeconds(4);
                Say("Frame rate: " + HzName(frmHz) + " Hz", 4000);
            }
        }

        // Video Processor: follow the source when its frame rate changes.
        void FrmCaptureTick()
        {
            double fps = 0;
            try { fps = EngineFps(true); } catch (Exception) { }
            if (fps <= 1 || Math.Abs(fps - capFpsLast) < 0.01) return;
            capFpsLast = fps;
            if (FrmApply(fps)) ShowInfo(8000);   // 2.70.0: Info shows the projector's new rate
        }

        // ---- 2.66.0: Calibrate a projector (any projector, by name) --------------------------
        // The meter (ClipGuardMeter.exe in the Player folder) does the run with the player's own
        // ArgyllCMS; the .ccss, the LUT and the report are in the Calibration folder.
        public static string CalDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Calibration"); } }
        static string PlayerDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }
        int calState;            // 0 none, 1 typing the projector's name, 2 choosing the correction
        string calName = "";
        int calCorr;             // 0 = <name>.ccss, 1 = none
        bool calRunning;

        string CalCcssPath() { return Path.Combine(CalDir, SafeName(calName) + ".ccss"); }

        static string SafeName(string n)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in n.Trim()) b.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return b.ToString();
        }

        static char KeyChar(Keys k)
        {
            Keys c = k & Keys.KeyCode;
            bool shift = (k & Keys.Shift) != 0;
            if (c >= Keys.A && c <= Keys.Z) { char ch = (char)('a' + (c - Keys.A)); return shift ? char.ToUpperInvariant(ch) : ch; }
            if (c >= Keys.D0 && c <= Keys.D9 && !shift) return (char)('0' + (c - Keys.D0));
            if (c >= Keys.NumPad0 && c <= Keys.NumPad9) return (char)('0' + (c - Keys.NumPad0));
            if (c == Keys.Space) return ' ';
            if (c == Keys.OemMinus || c == Keys.Subtract) return shift ? '_' : '-';
            if (c == Keys.OemPeriod || c == Keys.Decimal) return '.';
            if (c == Keys.Oemplus && !shift) return '=';
            if (c == Keys.D9 && shift) return '(';
            if (c == Keys.D0 && shift) return ')';
            return '\0';
        }

        void CalBegin()
        {
            try { Directory.CreateDirectory(CalDir); } catch (Exception) { }
            calState = 1;
            Invalidate();
        }

        bool CalDlgKey(Keys k)
        {
            Keys c = k & Keys.KeyCode;
            if (c == Keys.Escape || c == Keys.BrowserBack) { calState = 0; Invalidate(); return true; }
            if (calState == 1)
            {
                if (c == Keys.Back) { if (calName.Length > 0) calName = calName.Substring(0, calName.Length - 1); }
                else if (c == Keys.Enter)
                {
                    if (SafeName(calName).Length == 0) return true;
                    calState = 2;
                    calCorr = File.Exists(CalCcssPath()) ? 0 : 1;
                }
                else
                {
                    char ch = KeyChar(k);
                    if (ch != '\0' && calName.Length < 48) calName += ch;
                }
            }
            else if (calState == 2)
            {
                if (c == Keys.Left || c == Keys.Right || c == Keys.Up || c == Keys.Down) calCorr = 1 - calCorr;
                else if (c == Keys.Back) calState = 1;
                else if (c == Keys.Enter)
                {
                    if (calCorr == 0 && !File.Exists(CalCcssPath())) return true;   // the file has to be there
                    CalLaunch();
                }
            }
            Invalidate();
            return true;
        }

        void CalLaunch()
        {
            string exe = Path.Combine(PlayerDir, "ClipGuardMeter.exe");
            calState = 0;
            if (!File.Exists(exe)) { message = "The calibration program (ClipGuardMeter.exe) is not in the Player folder."; Invalidate(); return; }
            if (!File.Exists(Path.Combine(PlayerDir, "spotread.exe"))) { message = "ArgyllCMS (spotread.exe) is not in the Player folder, so the calibration can't run."; Invalidate(); return; }
            string args = "--cal \"" + calName.Replace("\"", "") + "\" --ccss \"" + (calCorr == 0 ? CalCcssPath() : "none")
                + "\" --out \"" + CalDir + "\" --argyll \"" + PlayerDir + "\" --screen \"" + UnpluggedScreen().DeviceName + "\"";
            Process p;
            try { p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = PlayerDir }); }
            catch (Exception ex) { message = "The calibration could not start: " + ex.Message; Invalidate(); return; }
            calRunning = true;
            Hide();
            Thread t = new Thread(delegate()
            {
                try { p.WaitForExit(); } catch (Exception) { }
                BeginInvoke((MethodInvoker)delegate
                {
                    calRunning = false;
                    Show();
                    Activate();
                    message = "Calibration closed. A new LUT is chosen here: Calibration LUT > LUT.";
                    Invalidate();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void DrawCalDlg(Graphics g)
        {
            RectangleF r = new RectangleF(300, 300, 1320, 420);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(250, 30, 32, 38))) g.FillRectangle(b, r);
            using (SolidBrush y = new SolidBrush(Yellow)) g.FillRectangle(y, r.X, r.Y, r.Width, 5);
            Txt(g, "Calibrate a projector", 30, true, Color.White, r.X + 40, r.Y + 30);
            if (calState == 1)
            {
                Txt(g, "The projector's name (type it, Enter when done):", 24, false, Grey, r.X + 40, r.Y + 100);
                RectangleF box = new RectangleF(r.X + 40, r.Y + 150, r.Width - 80, 64);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, box);
                Txt(g, calName + "_", 30, true, Color.White, box.X + 20, box.Y + 12);
                Txt(g, "Backspace deletes.   Esc cancels.", 20, false, Grey, r.X + 40, r.Bottom - 60);
            }
            else
            {
                string ccss = SafeName(calName) + ".ccss";
                bool have = File.Exists(CalCcssPath());
                Txt(g, "Projector:  " + calName, 26, true, Color.White, r.X + 40, r.Y + 95);
                Txt(g, "Correction (Left / Right):", 24, false, Grey, r.X + 40, r.Y + 150);
                string[] opts = { ccss + (have ? "" : "   (not found)"), "None (the meter's standard reading)" };
                for (int i = 0; i < 2; i++)
                {
                    RectangleF o = new RectangleF(r.X + 40, r.Y + 195 + i * 62, r.Width - 80, 54);
                    using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, o);
                    Txt(g, opts[i], 24, i == calCorr, i == calCorr ? Color.White : Grey, o.X + 20, o.Y + 12);
                    if (i == calCorr) Focus(g, o);
                }
                string note = calCorr == 0
                    ? (have ? "Enter starts the calibration on the projector." : "Put " + ccss + " in " + CalDir + ", or choose None.")
                    : "No .ccss: accuracy depends on how well the meter matches this projector's light. Enter starts.";
                TextBox(g, note + "   Backspace: name.   Esc cancels.", 20, false, Grey, new RectangleF(r.X + 40, r.Bottom - 70, r.Width - 80, 60));
            }
        }

        // The LUTs to choose from: none, every .cube in the Calibration folder, and the Titan /
        // Valerion LUTs from ClipGuard Meter's auto-cal.
        List<string> LutChoices()
        {
            List<string> l = new List<string>();
            l.Add("");
            try { foreach (string f in Directory.GetFiles(CalDir, "*.cube")) l.Add(f); } catch (Exception) { }
            foreach (string dir in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Unplugged", "filters"), appDir })
                foreach (string n in new[] { "titan.cube", "valerion.cube" })
                {
                    string f = Path.Combine(dir, n);
                    if (File.Exists(f) && !l.Exists(delegate(string x) { return string.Equals(Path.GetFileName(x), n, StringComparison.OrdinalIgnoreCase); })) l.Add(f);
                }
            return l;
        }

        static string LutName(string f)
        {
            if (string.IsNullOrEmpty(f)) return "None";
            string n = Path.GetFileNameWithoutExtension(f);
            return n == "titan" ? "Titan (ClipGuard auto-cal)" : n == "valerion" ? "Valerion (ClipGuard auto-cal)" : n;
        }

        void LutStep(int dir)
        {
            List<string> l = LutChoices();
            string cur = UnpluggedSettings.LutFile();
            int i = l.FindIndex(delegate(string x) { return string.Equals(x, cur, StringComparison.OrdinalIgnoreCase); });
            if (i < 0) i = 0;
            i = ((i + dir) % l.Count + l.Count) % l.Count;
            UnpluggedSettings.SetLut(l[i]);
        }

        // F5 while watching: the LUT off and on, for comparing (2.66.0, in the player).
        bool lutCompareOff;
        public void ToggleCubeLut()
        {
            if (player == null) return;
            lutCompareOff = !lutCompareOff;
            if (lutCompareOff) Engine.Cmd("{\"a\":\"lut\",\"file\":\"\",\"mode\":0}");
            else UnpluggedSettings.PushLut();
            string f = UnpluggedSettings.LutFile();
            string t = lutCompareOff ? "LUT off (F5 turns it back on)" : (f.Length > 0 ? "LUT on: " + LutName(f) : "No LUT chosen");
            if (capturing) CapCard("LUT", t, 2500); else Say(t, 2500);
        }

        // ---- 2.64.1: the ClipGuard tab: the F7 settings (shared with F7, live) ----------------
        int cgIdx;
        bool CgTabKey(Keys k)
        {
            IPlayerHost h = PHost;
            int n = h != null ? h.TabRowCount() : 0;
            if (k == Keys.Up) { if (cgIdx == 0) navFocus = true; else cgIdx--; }
            else if (k == Keys.Down) cgIdx = Math.Min(Math.Max(0, n - 1), cgIdx + 1);
            else if ((k == Keys.Left || k == Keys.Right || k == Keys.Enter) && h != null && cgIdx < n) h.TabRowStep(cgIdx, k == Keys.Left ? -1 : 1);
            else return false;
            Invalidate();
            return true;
        }

        static readonly string[] CgHelp = {
            "ClipGuard on or off (F8 while watching).",
            "Universal (Reactive) works with any laser projector. Titan Noir Max DBLE High / Low (Predictive) predict the Titan's DBLE; set the projector's DBLE to the same mode.",
            "How strongly highlights are protected.", "Normal or Anchor.", "Picture brightness where the fix begins.", "Picture brightness at and under which it is full.",
            "Where highlights start.", "0 = strongest channel, 1 = brightness.", "Holds off when this much of the highlights is bright text on black.",
            "Local cut on small, very bright spots (F10 while watching turns it off and on).", "No hot spots above this picture brightness.", "Patch or Pixel (only the light, feathered).",
            "Gentle dimming of small graded highlights.", "Yes: clearly orange or red lights (flames, candles) are dimmed too.", "The measuring grid.",
            "Frames the picture is held back for films. The player delays the sound to match.",
            "Frames the picture is held back for Video Processor. Off for games. Sound through the receiver: set that input's delay there to match; game sound through the capture card follows it by itself.",
            "Static keeps the fine dither pattern still; Temporal moves it every frame." };

        void DrawCgTab(Graphics g)
        {
            IPlayerHost h = PHost;
            int n = h != null ? h.TabRowCount() : 0;
            const float rowH = 50;
            int vis = 14;
            int first = Math.Max(0, Math.Min(cgIdx - vis / 2, n - vis));
            for (int r = 0; r < vis && first + r < n; r++)
            {
                int i = first + r;
                float y = 128 + r * (rowH + 6);
                RectangleF rr = new RectangleF(96, y, 1728, rowH);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, rr);
                Txt(g, h.TabRowName(i), 22, true, Color.White, 126, y + 11);
                Txt(g, h.TabRowValue(i), 22, false, i == cgIdx ? Color.White : Grey, 820, y + 11);
                if (!navFocus && i == cgIdx) Focus(g, rr);
            }
            string help = cgIdx < CgHelp.Length ? CgHelp[cgIdx] : "";
            TextBox(g, help + "   Left / Right changes it now; F7 shows the same settings while watching.", 20, false, Grey, new RectangleF(96, 924, 1728, 90));
        }

        // ---- 2.68.0: TV shows: a show's seasons and episodes ----------------------------------
        Movie showCur;
        List<Movie> seasons = new List<Movie>(), episodes = new List<Movie>();
        int seasonIdx, epIdx, epScroll;
        bool onSeasons, showLoading;
        bool endedNat;
        Movie nextEp;
        DateTime nextAt = DateTime.MaxValue;
        System.Windows.Forms.Timer nextTimer;

        void OpenShow(Movie s)
        {
            showCur = s;
            seasons = new List<Movie>(); episodes = new List<Movie>();
            seasonIdx = 0; epIdx = 0; epScroll = 0; onSeasons = false;
            showLoading = true;
            scr = Screen2.Show;
            List<Movie> ss = new List<Movie>();
            Bg2(delegate
            {
                object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(s.Srv, "/library/metadata/" + s.Key + "/children"), s.Srv.Token, 15000)), "MediaContainer");
                foreach (object m in Plex.Arr(mc, "Metadata"))
                {
                    if (Plex.Str(m, "type") != "season") continue;
                    Movie se = Plex.ToMovie(m); se.Srv = s.Srv; se.Type = "season"; se.ShowTitle = s.Title; se.ShowKey = s.Key;
                    ss.Add(se);
                }
                ss.Sort(delegate(Movie a, Movie b) { return a.SeasonNum.CompareTo(b.SeasonNum); });
            }, delegate
            {
                if (showCur != s) return;
                seasons = ss;
                // The first season with something left to watch (specials last).
                seasonIdx = 0;
                for (int i = 0; i < ss.Count; i++)
                    if (ss[i].SeasonNum > 0 && ss[i].ViewedLeafCount < ss[i].LeafCount) { seasonIdx = i; break; }
                if (ss.Count > 0 && ss[seasonIdx].SeasonNum == 0)
                    for (int i = 0; i < ss.Count; i++) if (ss[i].SeasonNum > 0) { seasonIdx = i; break; }
                showLoading = false;
                LoadEpisodes();
            }, delegate(Exception ex) { showLoading = false; message = ex.Message; Invalidate(); });
        }

        void LoadEpisodes()
        {
            if (showCur == null || seasonIdx < 0 || seasonIdx >= seasons.Count) { Invalidate(); return; }
            Movie se = seasons[seasonIdx];
            Movie show = showCur;
            List<Movie> eps = new List<Movie>();
            bool keepPos = episodes.Count > 0 && episodes[0].SeasonKey == se.Key;
            Bg2(delegate
            {
                object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(se.Srv, "/library/metadata/" + se.Key + "/children"), se.Srv.Token, 15000)), "MediaContainer");
                foreach (object m in Plex.Arr(mc, "Metadata"))
                {
                    Movie e = Plex.ToMovie(m); e.Srv = se.Srv; e.Type = "episode";
                    if (e.ShowTitle.Length == 0) e.ShowTitle = show.Title;
                    if (e.ShowKey.Length == 0) e.ShowKey = show.Key;
                    if (e.SeasonKey.Length == 0) e.SeasonKey = se.Key;
                    if (e.ShowThumb.Length == 0) e.ShowThumb = show.Thumb;
                    if (e.SeasonNum == 0) e.SeasonNum = se.SeasonNum;
                    eps.Add(e);
                }
                eps.Sort(delegate(Movie a, Movie b) { return a.EpNum.CompareTo(b.EpNum); });
            }, delegate
            {
                if (showCur != show || seasonIdx >= seasons.Count || seasons[seasonIdx] != se) return;
                episodes = eps;
                if (!keepPos)
                {
                    // Where you are: a started episode, else the first one not watched.
                    epIdx = 0;
                    int started = eps.FindIndex(delegate(Movie e) { return e.Offset > 0; });
                    int unseen = eps.FindIndex(delegate(Movie e) { return e.ViewCount == 0; });
                    if (started >= 0) epIdx = started; else if (unseen >= 0) epIdx = unseen;
                }
                epIdx = Math.Min(epIdx, Math.Max(0, eps.Count - 1));
                Invalidate();
            }, delegate(Exception ex) { message = ex.Message; Invalidate(); });
        }

        bool ShowKey(Keys k, bool back)
        {
            if (back)
            {
                if (showFrom == Screen2.Home) { showFrom = Screen2.Shows; scr = Screen2.Home; tab = 0; navFocus = false; return true; }   // 2.71.0
                scr = Screen2.Shows; tab = TabTV; navFocus = false; return true;
            }
            if (onSeasons)
            {
                if ((k == Keys.Left || k == Keys.Right) && seasons.Count > 0)
                {
                    int n = Math.Max(0, Math.Min(seasons.Count - 1, seasonIdx + (k == Keys.Right ? 1 : -1)));
                    if (n != seasonIdx) { seasonIdx = n; episodes = new List<Movie>(); epIdx = 0; epScroll = 0; LoadEpisodes(); }
                }
                else if (k == Keys.Down || k == Keys.Enter) { if (episodes.Count > 0) onSeasons = false; }
                else if (k == Keys.Up) { }
                else return false;
                return true;
            }
            if (k == Keys.Up) { if (epIdx == 0) onSeasons = true; else epIdx--; }
            else if (k == Keys.Down) epIdx = Math.Min(Math.Max(0, episodes.Count - 1), epIdx + 1);
            else if (k == Keys.PageDown) epIdx = Math.Min(Math.Max(0, episodes.Count - 1), epIdx + 8);
            else if (k == Keys.PageUp) epIdx = Math.Max(0, epIdx - 8);
            else if (k == Keys.Left || k == Keys.Right) { onSeasons = true; return ShowKey(k, false); }
            else if (k == Keys.Enter && epIdx < episodes.Count) OpenDetails(episodes[epIdx], Screen2.Show);
            else return false;
            return true;
        }

        void DrawShowPage(Graphics g)
        {
            Movie s = showCur;
            if (s == null) return;
            Image art = Img(s, s.Art, 1920, 1080);
            if (art != null)
            {
                g.DrawImage(art, new RectangleF(0, 0, 1920, 1080));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(215, Bg))) g.FillRectangle(b, 0, 0, 1920, 1080);
            }
            Poster(g, s, 0, 96, 120, 300, 450, false);
            float x = 460;
            TextBox(g, s.Title, 48, true, Color.White, new RectangleF(x, 110, 1360, 70));
            if (showLoading) { Txt(g, "Loading...", 24, false, Grey, x, 200); return; }
            // seasons
            float sx = x;
            using (Font f = F(24, true))
                for (int i = 0; i < seasons.Count; i++)
                {
                    string name = seasons[i].SeasonNum == 0 ? "Specials" : "Season " + seasons[i].SeasonNum.ToString(CultureInfo.InvariantCulture);
                    SizeF sz = g.MeasureString(name, f);
                    if (sx + sz.Width > 1840) break;
                    bool cur = i == seasonIdx;
                    using (SolidBrush b = new SolidBrush(cur ? Color.White : Grey)) g.DrawString(name, f, b, sx, 196);
                    if (cur) using (SolidBrush y = new SolidBrush(Yellow)) g.FillRectangle(y, sx + 4, 234, sz.Width - 8, 4);
                    if (cur && onSeasons) Focus(g, new RectangleF(sx - 4, 194, sz.Width + 8, 40));
                    sx += sz.Width + 36;
                }
            // episodes
            const float rowH = 66;
            int vis = 9;
            if (epIdx < epScroll) epScroll = epIdx;
            if (epIdx >= epScroll + vis) epScroll = epIdx - vis + 1;
            if (episodes.Count == 0) Txt(g, "Loading episodes...", 22, false, Grey, x, 270);
            for (int r = 0; r < vis && epScroll + r < episodes.Count; r++)
            {
                int i = epScroll + r;
                Movie e = episodes[i];
                float y = 262 + r * (rowH + 6);
                RectangleF rr = new RectangleF(x, y, 1360, rowH);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, rr);
                Txt(g, e.EpNum.ToString(CultureInfo.InvariantCulture), 24, true, Grey, x + 24, y + 17);
                TextBox(g, e.Title, 24, true, Color.White, new RectangleF(x + 96, y + 17, 900, 36));
                string right = e.ViewCount > 0 && e.Offset == 0 ? "Watched" : (e.Offset > 0 && e.Duration > 0 ? Left(e.Duration - e.Offset) + " left" : (e.Duration > 0 ? Left(e.Duration) : ""));
                TxtR(g, right, 20, false, e.ViewCount > 0 && e.Offset == 0 ? Grey : Color.FromArgb(220, 222, 226), rr.Right - 24, y + 21);
                if (e.Offset > 0 && e.Duration > 0)
                {
                    using (SolidBrush b = new SolidBrush(Dim)) g.FillRectangle(b, x + 96, y + rowH - 8, 900, 4);
                    using (SolidBrush b = new SolidBrush(Yellow)) g.FillRectangle(b, x + 96, y + rowH - 8, 900 * Math.Min(1f, (float)e.Offset / e.Duration), 4);
                }
                if (!onSeasons && i == epIdx) Focus(g, rr);
            }
            if (!onSeasons && epIdx < episodes.Count)
                TextBox(g, episodes[epIdx].Summary, 20, false, Color.FromArgb(200, 203, 209), new RectangleF(x, 262 + vis * (rowH + 6) + 8, 1360, 90));
            Txt(g, "Up / Down: episode    Left / Right: season    Enter: open    Backspace: back", 18, false, Grey, 96, 1036);
        }

        // The episode after this one: the next in its season, else the first of the next season.
        void NextUp(Movie m)
        {
            Movie found = null;
            Bg2(delegate
            {
                object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + m.SeasonKey + "/children"), m.Srv.Token, 15000)), "MediaContainer");
                foreach (object o in Plex.Arr(mc, "Metadata"))
                {
                    Movie e = Plex.ToMovie(o);
                    if (e.EpNum > m.EpNum && (found == null || e.EpNum < found.EpNum)) found = e;
                }
                if (found == null && m.ShowKey.Length > 0)
                {
                    object sc = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + m.ShowKey + "/children"), m.Srv.Token, 15000)), "MediaContainer");
                    Movie nextSeason = null;
                    foreach (object o in Plex.Arr(sc, "Metadata"))
                    {
                        if (Plex.Str(o, "type") != "season") continue;
                        Movie se = Plex.ToMovie(o);
                        if (se.SeasonNum > m.SeasonNum && (nextSeason == null || se.SeasonNum < nextSeason.SeasonNum)) nextSeason = se;
                    }
                    if (nextSeason != null)
                    {
                        object ec = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + nextSeason.Key + "/children"), m.Srv.Token, 15000)), "MediaContainer");
                        foreach (object o in Plex.Arr(ec, "Metadata"))
                        {
                            Movie e = Plex.ToMovie(o);
                            if (found == null || e.EpNum < found.EpNum) found = e;
                        }
                    }
                }
                if (found != null)
                {
                    found.Srv = m.Srv; found.Type = "episode";
                    if (found.ShowTitle.Length == 0) found.ShowTitle = m.ShowTitle;
                    if (found.ShowKey.Length == 0) found.ShowKey = m.ShowKey;
                    if (found.ShowThumb.Length == 0) found.ShowThumb = m.ShowThumb;
                }
            }, delegate
            {
                if (found == null || scr != Screen2.Details || det != m) return;
                OpenDetails(found, detailsFrom);
                nextEp = found;
                nextAt = DateTime.Now.AddSeconds(10);
                if (nextTimer == null)
                {
                    nextTimer = new System.Windows.Forms.Timer();
                    nextTimer.Interval = 250;
                    nextTimer.Tick += delegate
                    {
                        if (nextAt == DateTime.MaxValue) { nextTimer.Stop(); return; }
                        if (scr != Screen2.Details || det != nextEp) { nextAt = DateTime.MaxValue; nextTimer.Stop(); return; }
                        Invalidate();
                        if (DateTime.Now >= nextAt) { nextAt = DateTime.MaxValue; nextTimer.Stop(); Play(det, 0); }
                    };
                }
                nextTimer.Start();
                Invalidate();
            }, delegate(Exception ex) { });
        }

        void Launch(string file, long startMs, Movie m, bool lip)
        {
            playing = m;
            lipSync = lip;
            lipDelta = 0;
            lastPos = -1;
            lastDur = 0;
            sentDelay = int.MinValue;
            seekTarget = -1;
            curUrl = file;
            curVer = m != null && m.Versions.Count > 0 ? m.Versions[Math.Max(0, Math.Min(m.Versions.Count - 1, m.VerIdx))] : null;
            // 2.57.0: start on the sound and subtitles Plex has chosen for this film.
            audioCur = PlexTrack(curVer == null ? null : curVer.Audios);
            subCur = PlexTrack(curVer == null ? null : curVer.Subs);
            audioNames = new List<string>(); subNames = new List<string>(); engAudio = -1; engSub = -1; tracksKnown = false;
            reconnects = 0; reconnecting = false; paused = false;
            lastMovePos = -1; lastMove = DateTime.Now;
            menuOpen = false;
            bif = null; bifImg = null; bifImgIdx = -1;
            logoImg = null;
            barUntil = DateTime.MinValue; msgUntil = DateTime.MinValue;
            capturing = false;
            frmDone = false; frmResumeAt = DateTime.MaxValue; frmResumePos = -1; lutCompareOff = false;
            if (PHost != null) PHost.SetPlayerSource(1);   // 2.64.1: the Media Player's look-ahead
            EnsureVideo();
            Hide();
            string f = file;
            pushedGen = -1;
            video.BeginInvoke((MethodInvoker)delegate
            {
                // 2.53.1: Unplugged's processing settings first (as Unplugged pushes them);
                // the renderer takes them while the graph is built.
                UnpluggedSettings.Push();
                ApplyDelay();   // 2.56.0: lip sync, kept by the engine for the graph it builds
                int rc = OpenUrl(f);
                if (rc != 0)
                {
                    player = null;
                    scr = lip ? Screen2.Settings : Screen2.Details;
                    message = "The player could not open " + (lip ? "the lip sync clip" : "\"" + m.Title + "\"") + " (0x" + rc.ToString("X8") + "). The engine's log: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unplugged", "logs", "engine.log");
                    ShowOverVideo();   // 2.70.0
                    return;
                }
                player = Process.GetCurrentProcess();   // "a film is playing"
                // 2.55.0: resume where Plex says you stopped.
                if (startMs > 0) Engine.Cmd("{\"a\":\"seek\",\"pos\":" + (startMs * 10000L).ToString(CultureInfo.InvariantCulture) + "}");
                lastPos = startMs > 0 ? startMs : 0;
                lastDur = m != null ? m.Duration : 0;
                tickN = 0;
                engState = "";
                playTimer.Interval = 500;
                playTimer.Start();
                if (lip) Say("Lip sync test:  where is the yellow marker at the beep?  Left / Right moves the sound 10 ms (beep on the left: Right),  Enter saves,  Esc leaves", 10000);
                else
                {
                    ShowBar(5000);
                    LoadArtwork(m, curVer);
                }
            });
        }

        // 2.57.0: "fresh": never resumes the last position of the same file (playing a film
        // again from the start used to land where it last stopped); the chosen tracks.
        int OpenUrl(string url)
        {
            StringBuilder j = new StringBuilder("{\"a\":\"open\",\"fresh\":1");
            if (audioCur >= 0) j.Append(",\"atrack\":").Append(audioCur.ToString(CultureInfo.InvariantCulture));
            if (subCur >= 0) j.Append(",\"strack\":").Append(subCur.ToString(CultureInfo.InvariantCulture));
            j.Append(",\"url\":\"").Append(Engine.Esc(url)).Append("\"}");
            return Engine.Cmd(j.ToString());
        }

        // The engine's number (its place among the file's own streams of that kind) of the
        // stream Plex has selected; -1 when none is, or it is a separate file.
        static int PlexTrack(List<PStream> l)
        {
            List<PStream> e = Embedded(l);
            for (int i = 0; i < e.Count; i++) if (e[i].Selected) return i;
            return -1;
        }

        static List<PStream> Embedded(List<PStream> l)
        {
            List<PStream> e = new List<PStream>();
            if (l == null) return e;
            foreach (PStream p in l) if (!p.External && p.Index >= 0) e.Add(p);
            e.Sort(delegate(PStream a, PStream b) { return a.Index.CompareTo(b.Index); });
            return e;
        }

        int pushedGen = -1, tickN;
        string engState = "";
        void PlayTick()
        {
            if (player == null) { playTimer.Stop(); return; }
            if (capturing) { CaptureTick(); return; }
            int gen;
            long pos, dur;
            string st = Engine.State(out gen, out pos, out dur);
            FrmFilmTick(st);   // 2.65.0: frame rate matching
            // Unplugged re-pushes its settings on every graph generation (a rebuild drops them).
            if (gen >= 1 && gen != pushedGen) { pushedGen = gen; UnpluggedSettings.Push(); }
            ApplyDelay();   // follows ClipGuard's look-ahead when it is changed in the F7 menu
            paused = st == "paused";
            if ((tickN & 3) == 0) RefreshTracks();
            Movie m = playing;
            // 2.57.0: network trouble: reconnect at the same spot instead of stopping.
            if (m != null && !lipSync)
            {
                if (reconnects > 0 && (DateTime.Now - lastReconnect).TotalSeconds > 120) reconnects = 0;
                bool early = st == "ended" && lastDur > 0 && lastPos < lastDur - 300000;   // the stream ended, the film didn't
                if (st == "error" || early) { Reconnect(); return; }
                if (st == "playing")
                {
                    if (pos != lastMovePos)
                    {
                        if (lastMovePos >= 0 && reconnecting) { reconnecting = false; Say("Connected again", 2000); }
                        lastMovePos = pos;
                        lastMove = DateTime.Now;
                    }
                    else if ((DateTime.Now - lastMove).TotalSeconds > 30) { Reconnect(); return; }
                }
                else if (st == "opening") { if ((DateTime.Now - lastMove).TotalSeconds > 60) { Reconnect(); return; } }
                else lastMove = DateTime.Now;   // paused: not a stall
            }
            if (st == "ended" || st == "error")
            {
                if (st == "ended" && lastDur > 0) lastPos = lastDur;
                endedNat = st == "ended";   // 2.68.0: for the next episode
                StopFilm(false);
                return;
            }
            // 2.55.0: the position, and Plex told every 10 s and at play / pause.
            if (pos >= 0 && (st == "playing" || st == "paused")) { lastPos = pos / 10000; if (dur > 0) lastDur = dur / 10000; }
            if (m == null || lipSync) { tickN++; return; }
            bool changed = (st == "playing" || st == "paused") && st != engState && engState.Length > 0;
            if (st == "playing" || st == "paused") engState = st;
            if (++tickN >= 20 || changed)
            {
                tickN = 0;
                string state = st == "paused" ? "paused" : "playing";
                Bg2(delegate { Timeline(m, state); }, delegate { });
            }
        }

        void Reconnect()
        {
            reconnects++;
            lastReconnect = DateTime.Now;
            if (reconnects > 3)
            {
                Movie m = playing;
                StopFilm(false);
                if (m != null) message = "Playback stopped: the Plex server stopped sending \"" + m.Title + "\" (tried 3 times).";
                Invalidate();
                return;
            }
            long at = lastPos;
            Say("Connection lost: reconnecting (" + reconnects.ToString(CultureInfo.InvariantCulture) + " of 3)...", 8000);
            reconnecting = true;
            lastMovePos = -1;
            lastMove = DateTime.Now;
            pushedGen = -1;
            int rc = OpenUrl(curUrl);
            if (rc == 0 && at > 0) Engine.Cmd("{\"a\":\"seek\",\"pos\":" + (at * 10000L).ToString(CultureInfo.InvariantCulture) + "}");
        }

        // F4 / Back on the picture (or the film's end): stop and go back to the film's page.
        public void StopFilm(bool saveLip)
        {
            if (player == null) return;
            playTimer.Stop();
            seekTimer.Stop();
            menuOpen = false;
            if (lipSync && saveLip)
            {
                unpluggedDelay = ClampDelay(unpluggedDelay + lipDelta);
                SetIni(DelayKey, unpluggedDelay.ToString(CultureInfo.InvariantCulture));
            }
            Engine.Cmd("{\"a\":\"stopgraph\"}");
            barUntil = msgUntil = DateTime.MinValue;
            if (ovl != null) ovl.Hide();
            FilmEnded();
            ShowOverVideo();   // 2.70.0: the film's page over the picture, then the picture goes
        }

        // ---- 2.56.0: playback controls and lip sync; 2.57.0: on-screen display, track lists ----
        int sentDelay = int.MinValue;
        long seekTarget = -1;
        DateTime seekHold = DateTime.MinValue;
        readonly System.Windows.Forms.Timer seekTimer = new System.Windows.Forms.Timer();

        // The sound is held back by ClipGuard's look-ahead plus the player delay (and the
        // lip sync test's change while it runs); LAV Audio does it, + = sound later.
        void ApplyDelay()
        {
            int d = TotalDelay() + (lipSync ? lipDelta : 0);
            if (d == sentDelay) return;
            sentDelay = d;
            Engine.Cmd("{\"a\":\"set\",\"sets\":\"audio.delay=" + d.ToString(CultureInfo.InvariantCulture) + "\"}");
        }

        string msgText = "";
        DateTime msgUntil = DateTime.MinValue, barUntil = DateTime.MinValue;

        void Say(string text) { Say(text, 2500); }
        void Say(string text, int ms)
        {
            msgText = text;
            msgUntil = DateTime.Now.AddMilliseconds(ms);
            Kick();
        }

        void ShowBar(int ms)
        {
            if (lipSync || playing == null) return;
            DateTime t = DateTime.Now.AddMilliseconds(ms);
            if (t > barUntil) barUntil = t;
            Kick();
        }

        void Kick() { if (ovl != null) ovl.Kick(); }

        bool PreviewOn() { return seekTarget >= 0 && DateTime.Now < seekHold; }
        bool BarOn() { return !lipSync && playing != null && (DateTime.Now < barUntil || paused || PreviewOn()); }
        bool OverlayWanted() { return player != null && (DateTime.Now < msgUntil || BarOn() || menuOpen || (capturing && DateTime.Now < capUntil)); }

        long PosMs() { return PreviewOn() ? seekTarget / 10000 : Math.Max(0, lastPos); }
        long DurMs() { return Math.Max(lastDur, playing != null ? playing.Duration : 0); }

        // Keys on the picture. True when the key was used.
        public bool PlayerKey(Keys k, bool shift, bool ctrl)
        {
            if (player == null) { if (k == Keys.Escape && !ctrl) { Quit(); return true; } return false; }
            if (capturing) return CaptureKey(k, ctrl);
            if (menuOpen) return MenuKey(k);
            if (lipSync)
            {
                if (k == Keys.Left || k == Keys.Right)
                {
                    lipDelta = Math.Max(DelayMin - unpluggedDelay, Math.Min(1000 - unpluggedDelay, lipDelta + (k == Keys.Right ? 10 : -10)));
                    ApplyDelay();
                    Say("Sound " + Signed(lipDelta) + " ms   (audio delay would be " + (TotalDelay() + lipDelta).ToString(CultureInfo.InvariantCulture) + " ms;  Enter saves)", 4000);
                    return true;
                }
                if (k == Keys.Enter) { StopFilm(true); return true; }
                if (k == Keys.Escape || k == Keys.F4 || k == Keys.Back || k == Keys.BrowserBack) { StopFilm(false); return true; }
            }
            if (ctrl)
            {
                // Windows Media Center remote buttons arrive as these keys.
                switch (k)
                {
                    case Keys.P: if (shift) Play(true); else TogglePause(); return true;    // Play / Pause
                    case Keys.S: if (shift) { StopFilm(false); return true; } return false; // Stop
                    case Keys.F: if (shift) Skip(60); else ChapterJump(1); return true;     // Fast forward / Skip
                    case Keys.B: if (shift) Skip(-60); else ChapterJump(-1); return true;   // Rewind / Replay
                    case Keys.D: ToggleBar(); return true;                                   // More info
                    case Keys.G: OpenMenu(0); return true;                                   // Guide
                }
                return false;
            }
            switch (k)
            {
                case Keys.Escape: Quit(); return true;
                case Keys.F4: case Keys.Back: case Keys.BrowserBack: case Keys.MediaStop: StopFilm(false); return true;
                case Keys.Space: case Keys.Enter: case Keys.MediaPlayPause: TogglePause(); return true;
                case Keys.Play: Play(true); return true;
                case Keys.Pause: Play(false); return true;
                case Keys.Left: case Keys.Right:
                    {
                        int dir = k == Keys.Right ? 1 : -1;
                        if (paused && !shift)
                        {
                            Engine.Cmd("{\"a\":\"step\",\"dir\":" + dir.ToString(CultureInfo.InvariantCulture) + "}");
                            ShowBar(3000);
                            return true;
                        }
                        Skip(dir * (shift ? 60 : 10));
                        return true;
                    }
                case Keys.Up: Skip(60); return true;
                case Keys.Down: Skip(-60); return true;
                case Keys.PageUp: case Keys.MediaPreviousTrack: ChapterJump(-1); return true;
                case Keys.PageDown: case Keys.MediaNextTrack: ChapterJump(1); return true;
                case Keys.Home: SeekTo(0); return true;
                case Keys.I: ToggleBar(); return true;
                case Keys.A: OpenMenu(0); return true;
                case Keys.S: OpenMenu(1); return true;
                case Keys.T: case Keys.Apps: OpenMenu(0); return true;
                case Keys.OemOpenBrackets: case Keys.OemCloseBrackets:
                    unpluggedDelay = ClampDelay(unpluggedDelay + (k == Keys.OemCloseBrackets ? 5 : -5));
                    SetIni(DelayKey, unpluggedDelay.ToString(CultureInfo.InvariantCulture));
                    ApplyDelay();
                    Say("Audio delay " + TotalDelay().ToString(CultureInfo.InvariantCulture) + " ms", 3000);
                    return true;
            }
            if ((k >= Keys.D0 && k <= Keys.D9) || (k >= Keys.NumPad0 && k <= Keys.NumPad9))
            {
                int n = k >= Keys.NumPad0 ? k - Keys.NumPad0 : k - Keys.D0;
                long dur = DurMs();
                if (dur > 0) SeekTo(dur * 10000L / 10 * n);
                return true;
            }
            return false;
        }

        // Remote buttons that arrive as WM_APPCOMMAND.
        public bool PlayerAppCommand(int cmd)
        {
            if (player == null) return false;
            switch (cmd)
            {
                case 14: return PlayerKey(Keys.MediaPlayPause, false, false);   // play / pause
                case 46: Play(true); return true;                               // play
                case 47: Play(false); return true;                              // pause
                case 13: StopFilm(false); return true;                          // stop
                case 11: ChapterJump(1); return true;                           // next track
                case 12: ChapterJump(-1); return true;                          // previous track
                case 49: Skip(60); return true;                                 // fast forward
                case 50: Skip(-60); return true;                                // rewind
                case 1: return PlayerKey(Keys.BrowserBack, false, false);       // back
            }
            return false;
        }

        void TogglePause() { Play(paused); }

        void Play(bool play)
        {
            Engine.Cmd(play ? "{\"a\":\"play\"}" : "{\"a\":\"pause\"}");
            paused = !play;
            ShowBar(play ? 3000 : 0);
        }

        void ToggleBar()
        {
            if (BarOn() && !paused) { barUntil = DateTime.MinValue; Kick(); }
            else ShowBar(8000);
        }

        static string Signed(int v) { return (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture); }

        // Skips add up while a key is held; the seek itself goes once the presses stop.
        void Skip(int seconds)
        {
            SeekTo(PosMs() * 10000L + seconds * 10000000L);
        }

        void SeekTo(long t)
        {
            long dur = DurMs() * 10000L;
            if (dur > 0) t = Math.Min(t, Math.Max(0, dur - 50000000L));
            t = Math.Max(0, t);
            seekTarget = t;
            seekHold = DateTime.Now.AddMilliseconds(1500);
            if (seekTimer.Tag == null)
            {
                seekTimer.Tag = this;
                seekTimer.Interval = 300;
                seekTimer.Tick += delegate
                {
                    seekTimer.Stop();
                    if (player == null || seekTarget < 0) return;
                    Engine.Cmd("{\"a\":\"seek\",\"pos\":" + seekTarget.ToString(CultureInfo.InvariantCulture) + "}");
                    lastPos = seekTarget / 10000;
                    lastMove = DateTime.Now;
                    seekHold = DateTime.Now.AddMilliseconds(1500);
                    Kick();
                };
            }
            seekTimer.Stop();
            seekTimer.Start();
            ShowBar(4000);
        }

        // Chapters from Plex (with names and pictures); the file's own marks otherwise.
        void ChapterJump(int dir)
        {
            Movie m = playing;
            if (m == null || m.Chapters.Count < 2)
            {
                Engine.Cmd("{\"a\":\"chapter\",\"dir\":" + dir.ToString(CultureInfo.InvariantCulture) + "}");
                Say(dir > 0 ? "Next chapter" : "Chapter back", 1500);
                ShowBar(4000);
                return;
            }
            long pos = PosMs();
            int ci = ChapterAt(m, pos);
            long target;
            if (dir > 0)
            {
                if (ci + 1 >= m.Chapters.Count) { Say("Last chapter", 1500); return; }
                target = m.Chapters[ci + 1].Start;
            }
            else target = (ci >= 0 && pos - m.Chapters[ci].Start > 3000) ? m.Chapters[ci].Start : (ci > 0 ? m.Chapters[ci - 1].Start : 0);
            SeekTo(target * 10000L);
        }

        static int ChapterAt(Movie m, long ms)
        {
            int ci = -1;
            for (int i = 0; i < m.Chapters.Count; i++) if (m.Chapters[i].Start <= ms + 500) ci = i;
            return ci;
        }

        // ---- sound and subtitle tracks --------------------------------------------------
        List<string> audioNames = new List<string>(), subNames = new List<string>();
        int engAudio = -1, engSub = -1;
        bool tracksKnown, audioPlex, subPlex;   // *Plex: the list is Plex's (names and ids line up with the file)

        void RefreshTracks()
        {
            int ca, cs;
            List<string> ea = Tracks("audio", out ca), es = Tracks("subtitle", out cs);
            List<PStream> pa = Embedded(curVer == null ? null : curVer.Audios), ps = Embedded(curVer == null ? null : curVer.Subs);
            audioPlex = pa.Count > 0 && (ea.Count == 0 || ea.Count == pa.Count);
            subPlex = ps.Count > 0 && (es.Count == 0 || es.Count == ps.Count);
            audioNames = audioPlex ? pa.ConvertAll(delegate(PStream p) { return p.Name; }) : ea;
            subNames = subPlex ? ps.ConvertAll(delegate(PStream p) { return p.Name; }) : es;
            if (ea.Count > 0) { tracksKnown = true; engAudio = ca; }
            engSub = es.Count > 0 ? cs : subCur;
        }

        int AudioNow() { return audioCur >= 0 ? audioCur : (engAudio >= 0 ? engAudio : 0); }
        int SubNow() { return tracksKnown ? engSub : subCur; }

        string AudioLabel()
        {
            int i = AudioNow();
            return i >= 0 && i < audioNames.Count ? audioNames[i] : (audioNames.Count == 0 ? "-" : "Track " + (i + 1).ToString(CultureInfo.InvariantCulture));
        }

        string SubLabel()
        {
            int i = SubNow();
            return i < 0 ? "Off" : (i < subNames.Count ? subNames[i] : "Track " + (i + 1).ToString(CultureInfo.InvariantCulture));
        }

        bool menuOpen;
        int menuTab, menuIdx;

        void OpenMenu(int tab)
        {
            RefreshTracks();
            menuOpen = true;
            menuTab = tab;
            menuIdx = tab == 0 ? AudioNow() : SubNow() + 1;
            Kick();
        }

        // 2.63.5 (Player): the Sound list ends with the audio renderer's and LAV Audio's own
        // settings panels, for trying bitstream settings while the film plays.
#if PLAYER
        static readonly string[] AudioPanels = { "Audio renderer settings (MPC-BE)...", "LAV Audio settings..." };
#else
        static readonly string[] AudioPanels = new string[0];
#endif

        int MenuCount() { return menuTab == 0 ? audioNames.Count + AudioPanels.Length : subNames.Count + 1; }

        bool MenuKey(Keys k)
        {
            int n = MenuCount();
            switch (k)
            {
                case Keys.Up: if (menuIdx > 0) menuIdx--; break;
                case Keys.Down: if (menuIdx < n - 1) menuIdx++; break;
                case Keys.Left: case Keys.Right: case Keys.A: case Keys.S:
                    menuTab = k == Keys.A ? 0 : k == Keys.S ? 1 : 1 - menuTab;
                    menuIdx = menuTab == 0 ? AudioNow() : SubNow() + 1;
                    break;
                case Keys.Enter: case Keys.Space: UseTrack(); menuOpen = false; ShowBar(4000); break;
                case Keys.Escape: case Keys.Back: case Keys.BrowserBack: case Keys.F4: case Keys.T: case Keys.Apps:
                    menuOpen = false; break;
                default: return true;   // nothing else while the list is up
            }
            Kick();
            return true;
        }

        void UseTrack()
        {
            Movie m = playing;
            if (menuTab == 0 && menuIdx >= audioNames.Count && menuIdx < audioNames.Count + AudioPanels.Length)
            {
                AudioPanel(menuIdx - audioNames.Count);
                return;
            }
            if (menuTab == 0)
            {
                if (menuIdx < 0 || menuIdx >= audioNames.Count || menuIdx == AudioNow()) return;
                audioCur = menuIdx;
                Engine.Cmd("{\"a\":\"track\",\"index\":" + menuIdx.ToString(CultureInfo.InvariantCulture) + "}");
                Say("Sound: " + audioNames[menuIdx], 3000);
                if (audioPlex && m != null && curVer != null && curVer.PartId > 0)
                    RememberInPlex(m, "audioStreamID=" + Embedded(curVer.Audios)[menuIdx].Id.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                int s = menuIdx - 1;
                if (s == SubNow()) return;
                subCur = s;
                if (s >= 0) Engine.Cmd("{\"a\":\"subvisible\",\"on\":1}");
                Engine.Cmd("{\"a\":\"subtrack\",\"index\":" + s.ToString(CultureInfo.InvariantCulture) + "}");
                engSub = s;
                Say(s < 0 ? "Subtitles Off" : "Subtitles: " + subNames[s], 3000);
                if (subPlex && m != null && curVer != null && curVer.PartId > 0)
                    RememberInPlex(m, "subtitleStreamID=" + (s < 0 ? "0" : Embedded(curVer.Subs)[s].Id.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // 2.63.5: the filter's own panel (modal; the film keeps playing behind it). A change
        // to the renderer takes effect when the sound connects again, so the film is opened
        // again at the same spot after its panel closes. LAV Audio's changes are live.
        void AudioPanel(int f)
        {
            // 2.63.6: owned by the video window (the overlay never takes the keyboard) and
            // brought to the front with the keyboard while it is open.
            menuOpen = false;
            Kick();
            IntPtr owner = (video != null && video.IsHandleCreated) ? video.Handle : Handle;
            System.Windows.Forms.Timer front = new System.Windows.Forms.Timer();
            front.Interval = 150;
            int tries = 0;
            front.Tick += delegate
            {
                IntPtr h = OwnedPanel(owner);
                if (h != IntPtr.Zero && GetForegroundWindow() != h) { BringWindowToTop(h); SetForegroundWindow(h); }
                if ((h != IntPtr.Zero && GetForegroundWindow() == h) || ++tries > 40) front.Stop();
            };
            panelOpen = true;
            front.Start();
            int rc;
            try
            {
                rc = Engine.Cmd("{\"a\":\"props\",\"f\":" + f.ToString(CultureInfo.InvariantCulture) + ",\"hwnd\":" + owner.ToInt64().ToString(CultureInfo.InvariantCulture) + "}");
            }
            finally { front.Stop(); front.Dispose(); panelOpen = false; }
            if (owner == (video != null && video.IsHandleCreated ? video.Handle : IntPtr.Zero)) { SetForegroundWindow(owner); video.Activate(); }
            if (rc != 0) { Say("That settings panel could not be opened (0x" + rc.ToString("X8", CultureInfo.InvariantCulture) + ")", 4000); return; }
            if (f != 0 || player == null || string.IsNullOrEmpty(curUrl)) return;
            long at = lastPos;
            Say("Applying the audio renderer settings...", 3000);
            lastMovePos = -1;
            lastMove = DateTime.Now;
            pushedGen = -1;
            if (OpenUrl(curUrl) == 0 && at > 0) Engine.Cmd("{\"a\":\"seek\",\"pos\":" + (at * 10000L).ToString(CultureInfo.InvariantCulture) + "}");
        }

        // The choice is kept by Plex for this film (its own apps start on it too).
        void RememberInPlex(Movie m, string what)
        {
            long part = curVer.PartId;
            Bg2(delegate { Plex.Http("PUT", Url(m.Srv, "/library/parts/" + part.ToString(CultureInfo.InvariantCulture) + "?" + what + "&allParts=1"), m.Srv.Token, 8000); }, delegate { });
        }

        // The engine's track list ("audio" or "subtitle"): names, and the one in use.
        static List<string> Tracks(string group, out int current)
        {
            current = -1;
            List<string> names = new List<string>();
            string j = Engine.Stats();
            int a = j.IndexOf("\"" + group + "\":{", StringComparison.Ordinal);
            if (a < 0) return names;
            int b = j.IndexOf("]}", a, StringComparison.Ordinal);
            string part = b > a ? j.Substring(a, b - a) : j.Substring(a);
            Match cm = Regex.Match(part, "\"current\":(-?\\d+)");
            if (cm.Success) int.TryParse(cm.Groups[1].Value, out current);
            string str = "\"((?:[^\"\\\\]|\\\\.)*)\"";
            foreach (Match m in Regex.Matches(part, "\\{\"i\":(\\d+),\"name\":" + str + ",\"lang\":" + str + ",\"codec\":" + str + ",\"active\":(\\d)\\}"))
            {
                string name = Regex.Unescape(m.Groups[2].Value).Trim();
                string lang = Regex.Unescape(m.Groups[3].Value).Trim();
                string codec = Regex.Unescape(m.Groups[4].Value).Trim();
                if (name.Length == 0) name = lang.Length > 0 ? lang : "Track " + (names.Count + 1).ToString(CultureInfo.InvariantCulture);
                if (codec.Length > 0 && name.IndexOf(codec, StringComparison.OrdinalIgnoreCase) < 0) name += " (" + codec + ")";
                if (group == "audio" && m.Groups[5].Value == "1" && current < 0) current = names.Count;
                names.Add(name);
            }
            return names;
        }

        // ---- artwork: logo and preview thumbnails ------------------------------------------
        sealed class Bif { public long[] Ms; public int[] Off; public byte[] Data; }
        volatile Bif bif;
        Image bifImg;
        int bifImgIdx = -1;

        void LoadArtwork(Movie m, Ver v)
        {
            if (m == null || m.Srv == null) return;
            Server sv = m.Srv;
            string logo = m.Logo;
            long part = v != null && v.Indexes ? v.PartId : 0;
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (logo.Length > 0)
                {
                    try
                    {
                        byte[] b = Plex.Bytes(sv.Uri + "/photo/:/transcode?width=900&height=220&format=png&url=" + Uri.EscapeDataString(logo)
                            + "&X-Plex-Token=" + Uri.EscapeDataString(sv.Token), 15000);
                        Image im;
                        using (MemoryStream ms = new MemoryStream(b)) using (Image tmp = Image.FromStream(ms)) im = new Bitmap(tmp);
                        if (playing == m) logoImg = im;
                    }
                    catch (Exception) { }
                }
                if (part > 0)
                {
                    try
                    {
                        byte[] b = Plex.Bytes(sv.Uri + "/library/parts/" + part.ToString(CultureInfo.InvariantCulture) + "/indexes/sd?X-Plex-Token=" + Uri.EscapeDataString(sv.Token), 180000);
                        Bif x = ParseBif(b);
                        if (x != null && playing == m) bif = x;
                    }
                    catch (Exception) { }
                }
            });
        }

        static Bif ParseBif(byte[] b)
        {
            if (b == null || b.Length < 72 || b[0] != 0x89 || b[1] != (byte)'B' || b[2] != (byte)'I' || b[3] != (byte)'F') return null;
            int count = BitConverter.ToInt32(b, 12);
            long interval = BitConverter.ToUInt32(b, 16);
            if (interval == 0) interval = 1000;
            if (count <= 0 || 64L + (count + 1L) * 8 > b.Length) return null;
            Bif x = new Bif { Ms = new long[count], Off = new int[count + 1], Data = b };
            for (int i = 0; i <= count; i++)
            {
                long off = BitConverter.ToUInt32(b, 64 + i * 8 + 4);
                x.Off[i] = (int)Math.Min(off, b.Length);
                if (i < count) x.Ms[i] = BitConverter.ToUInt32(b, 64 + i * 8) * interval;
            }
            return x;
        }

        Image PreviewAt(long ms)
        {
            Bif x = bif;
            if (x == null) return null;
            int lo = 0, hi = x.Ms.Length - 1, i = 0;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (x.Ms[mid] <= ms) { i = mid; lo = mid + 1; } else hi = mid - 1; }
            if (i == bifImgIdx) return bifImg;
            try
            {
                int len = x.Off[i + 1] - x.Off[i];
                if (len <= 0) return bifImg;
                using (MemoryStream s = new MemoryStream(x.Data, x.Off[i], len)) using (Image tmp = Image.FromStream(s))
                {
                    Image im = new Bitmap(tmp);
                    if (bifImg != null) bifImg.Dispose();
                    bifImg = im;
                    bifImgIdx = i;
                }
            }
            catch (Exception) { }
            return bifImg;
        }

        // ---- drawing the on-screen display (1920 x 1080 units) ---------------------------
        void DrawOverlay(Graphics g, Size size)
        {
            float sc = size.Height / 1080f;
            float W = size.Width / sc;
            g.ScaleTransform(sc, sc);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            try
            {
                if (BarOn()) DrawBar(g, W);
                if (menuOpen) DrawMenu(g, W);
                if (DateTime.Now < msgUntil) DrawMsg(g, W);
                if (capturing && DateTime.Now < capUntil) DrawCapCard(g, W);
            }
            catch (Exception) { }
        }

        static void TxtR(Graphics g, string s, float size, bool bold, Color c, float right, float y)
        {
            using (Font f = F(size, bold)) using (SolidBrush b = new SolidBrush(c))
            {
                SizeF z = g.MeasureString(s, f);
                g.DrawString(s, f, b, right - z.Width, y);
            }
        }

        void DrawBar(Graphics g, float W)
        {
            Movie m = playing;
            long pos = PosMs(), dur = DurMs();
            const float H = 250;
            float top = 1080 - H;
            using (SolidBrush b = new SolidBrush(Bg)) g.FillRectangle(b, 0, top, W, H);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 73, 80))) g.FillRectangle(b, 0, top, W, 2);
            float x0 = 60, right = W - 60;
            // Poster
            RectangleF pr = new RectangleF(x0, top + 28, 126, 189);
            Image post = Img(m, m.Thumb, 252, 378);
            if (post != null) g.DrawImage(post, pr);
            else using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, pr);
            float x = pr.Right + 36;
            // Right block: state and end time, the chapter's picture left of it
            string state = paused ? "Paused" : (PreviewOn() ? "Going to " + Clock(seekTarget / 10000) : "Playing");
            TxtR(g, state, 30, true, paused ? Yellow : Color.White, right, top + 26);
            if (dur > 0) TxtR(g, "Ends at " + DateTime.Now.AddMilliseconds(Math.Max(0, dur - pos)).ToString("h:mm tt", CultureInfo.InvariantCulture), 24, false, Grey, right, top + 72);
            float titleRight = right - 330;
            int ci = ChapterAt(m, pos);
            if (ci >= 0)
            {
                Image ct = Img(m, m.Chapters[ci].Thumb, 384, 216);
                if (ct != null)
                {
                    RectangleF cr = new RectangleF(right - 330 - 192, top + 26, 192, 108);
                    g.DrawImage(ct, cr);
                    titleRight = cr.Left - 24;
                }
            }
            // Title: Plex's logo, or the name
            Image logo = logoImg;
            if (logo != null && logo.Height > 0)
            {
                float lh = 66, lw = lh * logo.Width / logo.Height;
                if (lw > Math.Min(560, titleRight - x)) { lw = Math.Min(560, titleRight - x); lh = lw * logo.Height / logo.Width; }
                g.DrawImage(logo, new RectangleF(x, top + 26 + (66 - lh) / 2, lw, lh));
            }
            else TextBox(g, m.Title + (m.Year > 0 ? "  (" + m.Year.ToString(CultureInfo.InvariantCulture) + ")" : ""), 34, true, Color.White, new RectangleF(x, top + 24, titleRight - x, 50));
            if (ci >= 0)
            {
                Chap c = m.Chapters[ci];
                string cl = "Chapter " + (ci + 1).ToString(CultureInfo.InvariantCulture) + " of " + m.Chapters.Count.ToString(CultureInfo.InvariantCulture);
                if (c.Title.Length > 0 && !c.Title.StartsWith("Chapter", StringComparison.OrdinalIgnoreCase)) cl += "   \u00b7   " + c.Title;
                TextBox(g, cl, 24, false, Grey, new RectangleF(x, top + 98, titleRight - x, 34));
            }
            // Progress bar with the chapter marks
            float by = top + 150, bx = x, bw = right - x;
            float frac = dur > 0 ? Math.Min(1f, (float)pos / dur) : 0f;
            using (SolidBrush b = new SolidBrush(Dim)) g.FillRectangle(b, bx, by, bw, 10);
            using (SolidBrush b = new SolidBrush(Yellow)) g.FillRectangle(b, bx, by, bw * frac, 10);
            if (dur > 0)
                using (SolidBrush b = new SolidBrush(Bg))
                    foreach (Chap c in m.Chapters)
                        if (c.Start > 0 && c.Start < dur) g.FillRectangle(b, bx + bw * c.Start / dur - 1, by, 3, 10);
            if (PreviewOn())
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, bx + bw * frac - 2, by - 8, 5, 26);
            Txt(g, Clock(pos), 26, true, Color.White, bx, by + 22);
            if (dur > 0) TxtR(g, "-" + Clock(Math.Max(0, dur - pos)), 26, true, Color.White, right, by + 22);
            using (Font f = F(22, false)) using (SolidBrush b = new SolidBrush(Grey)) using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString("Sound: " + AudioLabel() + "      Subtitles: " + SubLabel(), f, b, new RectangleF(bx + 200, by + 26, bw - 400, 32), sf);
            }
            // The picture where a skip lands (Plex's preview thumbnails)
            if (PreviewOn())
            {
                Image pv = PreviewAt(seekTarget / 10000);
                if (pv != null && pv.Width > 0)
                {
                    float pw = 384, ph = pw * pv.Height / pv.Width;
                    float px = Math.Max(20, Math.Min(W - pw - 20, bx + bw * frac - pw / 2)), py = top - ph - 28;
                    g.DrawImage(pv, new RectangleF(px, py, pw, ph));
                    using (Pen p = new Pen(Color.White, 3)) g.DrawRectangle(p, px - 1.5f, py - 1.5f, pw + 3, ph + 3);
                }
            }
        }

        void DrawMenu(Graphics g, float W)
        {
            int n = MenuCount();
            const float mw = 980, row = 58;
            int vis = Math.Min(Math.Max(n, 1), 10);
            float mh = 150 + vis * row + 70;
            float mx = (W - mw) / 2, my = Math.Max(40, (1080 - 250 - mh) / 2);
            using (SolidBrush b = new SolidBrush(Bg)) g.FillRectangle(b, mx, my, mw, mh);
            using (Pen p = new Pen(Color.FromArgb(70, 73, 80), 2)) g.DrawRectangle(p, mx, my, mw, mh);
            string[] tabs = { "Sound", "Subtitles" };
            float tx = mx + 40;
            using (Font f = F(30, true))
                for (int i = 0; i < 2; i++)
                {
                    SizeF z = g.MeasureString(tabs[i], f);
                    using (SolidBrush b = new SolidBrush(i == menuTab ? Color.White : Grey)) g.DrawString(tabs[i], f, b, tx, my + 30);
                    if (i == menuTab) using (SolidBrush y = new SolidBrush(Yellow)) g.FillRectangle(y, tx + 4, my + 74, z.Width - 8, 4);
                    tx += z.Width + 48;
                }
            int cur = menuTab == 0 ? AudioNow() : SubNow() + 1;
            int first = Math.Max(0, Math.Min(menuIdx - vis / 2, n - vis));
            for (int r = 0; r < vis && first + r < n; r++)
            {
                int i = first + r;
                string name = menuTab == 0 ? (i < audioNames.Count ? audioNames[i] : AudioPanels[i - audioNames.Count]) : (i == 0 ? "Off" : subNames[i - 1]);
                float ry = my + 110 + r * row;
                RectangleF rr = new RectangleF(mx + 30, ry, mw - 60, row - 8);
                if (i == menuIdx) using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, rr);
                if (i == cur) Txt(g, "\u25CF", 22, false, Yellow, rr.X + 12, ry + 10);
                TextBox(g, name, 26, i == cur, i == menuIdx ? Color.White : Color.FromArgb(220, 222, 226), new RectangleF(rr.X + 48, ry + 6, rr.Width - 60, row - 10));
                if (i == menuIdx) Focus(g, rr);
            }
            if (n == 0) Txt(g, "No tracks found yet", 26, false, Grey, mx + 78, my + 116);
            Txt(g, "Up / Down: choose    Enter: use    Left / Right: Sound / Subtitles    Back: close", 20, false, Grey, mx + 40, my + mh - 50);
        }

        void DrawMsg(Graphics g, float W)
        {
            using (Font f = F(30, false))
            {
                SizeF z = g.MeasureString(msgText, f);
                float w = Math.Min(W - 80, z.Width + 64), h = z.Height + 28;
                float x = (W - w) / 2, y = 60;
                using (SolidBrush b = new SolidBrush(Bg)) g.FillRectangle(b, x, y, w, h);
                using (Pen p = new Pen(Color.FromArgb(150, 150, 150), 2)) g.DrawRectangle(p, x, y, w, h);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(242, 242, 242))) using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(msgText, f, b, new RectangleF(x, y, w, h), sf);
                }
            }
        }

        // The on-screen display: a see-through window over the picture, shown only while
        // there is something on it (so the picture is presented on its own the rest of the time).
        sealed class Overlay : Form
        {
            static readonly Color KeyColor = Color.FromArgb(1, 0, 1);
            readonly MediaPlayer owner;
            readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
            bool owned;
            public Overlay(MediaPlayer owner)
            {
                this.owner = owner;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = KeyColor;
                TransparencyKey = KeyColor;
                DoubleBuffered = true;
                tick.Interval = 250;
                tick.Tick += delegate { Kick(); };
                tick.Start();
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x20; return cp; }   // no activate, tool window, clicks pass through
            }
            public void Kick()
            {
                Form v = owner.video;
                if (v == null || !v.Visible || !owner.OverlayWanted()) { if (Visible) Hide(); return; }
                if (Bounds != v.Bounds) Bounds = v.Bounds;
                bool shown = false;
                if (!owned) { owned = true; Show(v); shown = true; } else if (!Visible) { Show(); shown = true; }
                // 2.63.7: showing a TopMost form can still activate it, and the overlay never
                // takes keys: the keyboard goes back to the video window.
                if (shown && IsHandleCreated && GetForegroundWindow() == Handle) { SetForegroundWindow(v.Handle); v.Activate(); }
                Invalidate();
            }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(KeyColor);
                owner.DrawOverlay(e.Graphics, ClientSize);
            }
        }

        public void ExitToProcessor() { Quit(); }

        // 2.53.1: shutting down never hangs: the engine gets 3 seconds to stop, then the
        // process ends regardless.
        bool quitting;
        public void Quit()
        {
            if (quitting) return;
            quitting = true;
#if PLAYER
            // 2.70.0: why the player closed (Esc, the window closed, the source row...), in crash.log.
            try
            {
                System.Diagnostics.StackTrace tr = new System.Diagnostics.StackTrace(1, false);
                List<string> callers = new List<string>();
                for (int i = 0; i < tr.FrameCount && callers.Count < 6; i++)
                {
                    System.Reflection.MethodBase mb = tr.GetFrame(i).GetMethod();
                    if (mb != null) callers.Add(mb.DeclaringType.Name + "." + mb.Name);
                }
                PlayerProgram.LogCrash("closed (normal quit) from " + string.Join(" < ", callers.ToArray()), null);
            }
            catch (Exception) { }
#endif
#if PLAYER
            try { FrmRestore(); } catch (Exception) { }
#endif
            try { if (video != null) video.Hide(); Hide(); } catch (Exception) { }
#if PLAYER
            try { if (backdrop != null) backdrop.Hide(); } catch (Exception) { }
#endif
            Thread dog = new Thread(delegate() { Thread.Sleep(5000); Environment.Exit(0); });
            dog.IsBackground = true;
            dog.Start();
            // 2.72.0: if even that cannot finish (a driver call that never returns), the process
            // is ended outright, so it never stays behind holding the capture card or the sound.
            Thread dog2 = new Thread(delegate() { Thread.Sleep(9000); try { Process.GetCurrentProcess().Kill(); } catch (Exception) { } });
            dog2.IsBackground = true;
            dog2.Start();
            // 2.55.0: tell Plex where you stopped (and watched at 90%).
            Movie m = playing;
            if (m != null && !lipSync && lastPos >= 0)
            {
                try
                {
                    long dur = Math.Max(lastDur, m.Duration);
                    Timeline(m, "stopped");
                    if (dur > 0 && lastPos >= dur * 0.9)
                        Plex.Http("GET", Url(m.Srv, "/:/scrobble?identifier=com.plexapp.plugins.library&key=" + m.Key), m.Srv.Token, 3000);
                }
                catch (Exception) { }
            }
            try { if (player != null) Engine.Cmd("{\"a\":\"stopgraph\"}"); } catch (Exception) { }
            Engine.Shutdown();
            try { host.MediaClosed(); } catch (Exception) { }   // 2.54.0: ClipGuard saves its settings
            Environment.Exit(0);
        }


        // ---- Unplugged's processing settings (2.53.1) ------------------------------------
        // A port of Unplugged's LoadSettingsIni + PushAll + PushLut (native_ui.cpp): the
        // rail values from %LOCALAPPDATA%\Unplugged\s3_shell.ini (and the active preset in
        // presets\*.ini), each row's built-in default where the file has none, sent to the
        // engine in one "set" the way Unplugged sends them. Read-only: nothing of
        // Unplugged's is written. The row table is generated from Unplugged's g_rows.
        static class UnpluggedSettings
        {
            sealed class R
            {
                public int Grp, Kind, Opt, NOpts, NoEng; public string Key; public double Mn, Mx, Val; public bool On; public int[] OptVals;
                public R(int grp, int kind, string key, double mn, double mx, double val, bool on, int opt, int nopts, int[] optvals, int noeng)
                { Grp = grp; Kind = kind; Key = key; Mn = mn; Mx = mx; Val = val; On = on; Opt = opt; NOpts = nopts; OptVals = optvals; NoEng = noeng; }
                public R Copy() { return (R)MemberwiseClone(); }
                public string File = "";
            }

            static readonly R[] Rows = {
            new R(0,5,"pb.audioTrack",0.0,0.0,0.0,false,0,0,null,0),
            new R(0,2,"audio.mode",0.0,0.0,0.0,false,0,2,new[]{1,0},0),
            new R(0,1,"pb.audioDelay",-500.0,500.0,0.0,false,0,0,null,1),
            new R(0,7,"pb.subTrack",0.0,0.0,0.0,false,0,0,null,0),
            new R(1,3,"src.rescan",0.0,0.0,0.0,false,0,0,null,1),
            new R(1,3,"src.rename",0.0,0.0,0.0,false,0,0,null,1),
            new R(1,3,"src.setup",0.0,0.0,0.0,false,0,0,null,1),
            new R(2,0,"hdr.preferDoVi",0.0,0.0,0.0,true,0,0,null,0),
            new R(2,0,"hdr.convertToSdr",0.0,0.0,0.0,true,0,0,null,0),
            new R(2,1,"hdr.sdrNits",25.0,400.0,80.0,false,0,0,null,0),
            new R(2,2,"hdr.toneMapCurve",0.0,0.0,0.0,false,0,2,new[]{7,10},0),
            new R(2,1,"hdr.exposureTrim",-5.0,5.0,0.0,false,0,0,null,0),
            new R(2,1,"hdr.shadowLift",-5.0,5.0,0.0,false,0,0,null,0),
            new R(2,1,"hdr.punch",-5.0,5.0,0.0,false,0,0,null,0),
            new R(2,2,"hdr.outputGamut",0.0,0.0,0.0,false,0,2,new[]{0,1},0),
            new R(2,1,"hdr.gamutCap",0.2,1.0,1.0,false,0,0,null,0),
            new R(2,1,"hdr.hiDesat",0.0,1.0,0.0,false,0,0,null,0),
            new R(2,1,"hdr.titlePeak",100.0,4000.0,1000.0,false,0,0,null,0),
            new R(2,1,"hdr.softKnee",0.0,4.0,0.21,false,0,0,null,0),
            new R(2,1,"hdr.softKneeRatio",1.0,5.0,2.48,false,0,0,null,0),
            new R(2,1,"hdr.softKneeWidth",0.05,1.0,0.42,false,0,0,null,0),
            new R(2,0,"hdr.autoRefW",0.0,0.0,0.0,false,0,0,null,0),
            new R(2,1,"hdr.refWTgt",0.2,0.7,0.42,false,0,0,null,0),
            new R(2,1,"hdr.highlightNeutral",0.0,1.0,0.9,false,0,0,null,0),
            new R(2,1,"hdr.chromaExp",-1.0,3.5,1.35,false,0,0,null,0),
            new R(2,1,"hdr.gtBlend",0.0,1.0,0.6,false,0,0,null,0),
            new R(2,1,"hdr.gtFade",0.3,2.0,0.98,false,0,0,null,0),
            new R(2,1,"hdr.gtAdapt",0.0,1.0,0.25,false,0,0,null,0),
            new R(2,2,"hdr.gt7Dbg",0.0,0.0,0.0,false,0,5,null,0),
            new R(2,1,"hdr.gtAlpha",0.02,0.9,0.25,false,0,0,null,0),
            new R(2,1,"hdr.gtDesat",0.0,1.0,0.0,false,0,0,null,0),
            new R(2,1,"hdr.tmContrast",0.0,1.5,0.5,false,0,0,null,0),
            new R(2,1,"hdr.tmPivot",0.0,1.0,1.0,false,0,0,null,0),
            new R(2,2,"hdr.toggleDisplay",0.0,0.0,0.0,false,0,5,null,0),
            new R(2,1,"hdr.maxNits",10.0,1000.0,1000.0,false,0,0,null,0),
            new R(2,1,"hdr.blackNits",0.0,2.0,0.0,false,0,0,null,0),
            new R(2,1,"hdr.osdBright",0.0,100.0,0.0,false,0,0,null,0),
            new R(2,0,"postfx.sharpen",0.0,0.0,0.0,false,0,0,null,0),
            new R(2,1,"postfx.sharpenStr",0.3,1.5,1.0,false,0,0,null,0),
            new R(2,0,"postfx.deband",0.0,0.0,0.0,true,0,0,null,0),
            new R(2,1,"postfx.debandThresh",0.0,256.0,32.0,false,0,0,null,0),
            new R(2,1,"postfx.debandRange",1.0,64.0,16.0,false,0,0,null,0),
            new R(2,1,"postfx.debandIter",1.0,4.0,1.0,false,0,0,null,0),
            new R(2,1,"postfx.debandGrain",0.0,128.0,24.0,false,0,0,null,0),
            new R(3,0,"zion.enabled",0.0,0.0,0.0,true,0,0,null,0),
            new R(3,3,"zion.reset",0.0,0.0,0.0,false,0,0,null,1),
            new R(3,0,"zion.ctl",0.0,0.0,0.0,true,0,0,null,0),
            new R(3,2,"zion.toneMode",0.0,0.0,0.0,false,0,3,new[]{2,1,0},0),
            new R(3,1,"zion.shadowToe",0.0,0.4,0.0,false,0,0,null,0),
            new R(3,1,"zion.shadowDehaze",0.0,1.0,0.0,false,0,0,null,0),
            new R(3,1,"zion.gamma",0.5,1.5,1.0,false,0,0,null,0),
            new R(3,1,"zion.gammaTopUp",-0.5,0.5,0.0,false,0,0,null,0),
            new R(3,1,"zion.tgtMid",0.0,0.95,0.0,false,0,0,null,0),
            new R(3,1,"zion.highlight",-0.3,0.4,0.0,false,0,0,null,0),
            new R(3,1,"zion.highlightRolloffCtrl",-0.2,0.2,0.0,false,0,0,null,0),
            new R(3,2,"zion.chromaMode",0.0,0.0,0.0,false,0,3,null,1),
            new R(3,1,"zion.chN",1.0,1.3,1.08,false,0,0,null,0),
            new R(3,1,"zion.chF",1.0,1.3,1.15,false,0,0,null,0),
            new R(3,1,"zion.knee",0.1,0.9,0.45,false,0,0,null,0),
            new R(3,1,"zion.skin",0.5,1.0,0.95,false,0,0,null,0),
            new R(3,0,"zion.contrast",0.0,0.0,0.0,true,0,0,null,0),
            new R(3,1,"zion.contrastStrength",0.0,0.5,0.15,false,0,0,null,0),
            new R(3,1,"zion.contrastZoneLo",0.0,0.6,0.15,false,0,0,null,0),
            new R(3,1,"zion.contrastRatioCap",1.0,2.0,1.4,false,0,0,null,0),
            new R(3,1,"zion.contrastSkinProtect",0.0,1.0,0.65,false,0,0,null,0),
            new R(3,0,"hdr.hdrPass",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.debugCopyShaders",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.debugFrameLog",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.contrastDebugVis",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.barsRepaint",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.barsHighlight",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.skyEnabled",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,1,"zion.skyStrength",0.0,1.0,0.6,false,0,0,null,0),
            new R(3,0,"zion.skyOverlay",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,0,"zion.personEnabled",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,1,"zion.personStrength",0.0,1.0,1.0,false,0,0,null,0),
            new R(3,0,"zion.personOverlay",0.0,0.0,0.0,false,0,0,null,0),
            new R(3,1,"zion.skinPreserve",0.0,1.0,0.0,false,0,0,null,0),
            new R(4,0,"out.useD3D11",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,2,"out.texFormat",0.0,0.0,0.0,false,0,4,new[]{0,8,10,16},0),
            new R(4,0,"out.vpNV12",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.vpP01x",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.vpYUY2",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.vpOther",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,2,"out.deinterlace",0.0,0.0,0.0,false,1,3,null,0),
            new R(4,0,"out.deintDouble",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.deintBlend",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,0,"out.shaderVP",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.superRes",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,0,"out.rtxVideoHdr",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,2,"out.chromaScaling",0.0,0.0,0.0,false,1,2,new[]{2,3},0),
            new R(4,2,"out.upscaling",0.0,0.0,0.0,false,1,2,new[]{4,5},0),
            new R(4,2,"out.downscaling",0.0,0.0,0.0,false,0,2,new[]{5,4},0),
            new R(4,0,"out.interpolate50",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.useDither",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,2,"geo.mode",0.0,0.0,0.0,false,0,3,new[]{0,1,2},0),
            new R(4,1,"geo.aspect",1.0,3.0,1.78,false,0,0,null,0),
            new R(4,1,"geo.zoom",50.0,200.0,100.0,false,0,0,null,0),
            new R(4,1,"geo.panX",-100.0,100.0,0.0,false,0,0,null,0),
            new R(4,1,"geo.panY",-100.0,100.0,0.0,false,0,0,null,0),
            new R(4,0,"geo.nls",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,1,"geo.nlsBars",0.0,100.0,100.0,false,0,0,null,0),
            new R(4,2,"geo.nlsAxis",0.0,0.0,0.0,false,0,3,new[]{1,2,3},0),
            new R(4,1,"geo.nlsStrength",0.0,100.0,100.0,false,0,0,null,0),
            new R(4,1,"geo.nlsCenter",0.0,90.0,45.0,false,0,0,null,0),
            new R(4,1,"geo.nlsCurve",1.0,2.9,1.2,false,0,0,null,0),
            new R(4,1,"geo.nlsSplit",0.0,100.0,50.0,false,0,0,null,0),
            new R(4,1,"geo.nlsMax",100.0,200.0,140.0,false,0,0,null,0),
            new R(4,1,"geo.nlsCropX",0.0,45.0,0.0,false,0,0,null,0),
            new R(4,1,"geo.nlsCropY",0.0,45.0,0.0,false,0,0,null,0),
            new R(4,2,"geo.nlsFillMode",0.0,0.0,0.0,false,0,4,new[]{0,1,2,3},0),
            new R(4,0,"geo.nlsLock",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,2,"out.swapEffect",0.0,0.0,0.0,false,1,2,null,0),
            new R(4,0,"out.exclusiveFS",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,0,"out.vBlank",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,0,"out.adjustPresent",0.0,0.0,0.0,true,0,0,null,0),
            new R(4,0,"out.reinitDisplay",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,0,"out.showStats",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,3,"__capFrame",0.0,0.0,0.0,false,0,0,null,1),
            new R(4,4,"out.cubeLut",0.0,0.0,0.0,false,0,0,null,0),
            new R(4,2,"out.cubeLutMode",0.0,0.0,0.0,false,3,4,null,0),
            new R(5,0,"sys.trayIcon",0.0,0.0,0.0,false,0,0,null,0),
            new R(5,0,"sys.updateCheck",0.0,0.0,0.0,true,0,0,null,1),
            new R(5,3,"sys.updateNow",0.0,0.0,0.0,false,0,0,null,1),
            new R(5,3,"__report",0.0,0.0,0.0,false,0,0,null,1),
            new R(5,0,"ui.helpBanner",0.0,0.0,0.0,true,0,0,null,1),
            new R(5,0,"ui.autoNext",0.0,0.0,0.0,true,0,0,null,1),
            new R(5,0,"ui.showImdbRating",0.0,0.0,0.0,true,0,0,null,1),
            new R(5,3,"sys.resetRenderer",0.0,0.0,0.0,false,0,0,null,1),
            new R(5,3,"src.setup",0.0,0.0,0.0,false,0,0,null,1),
            };

            static readonly Dictionary<string, string> EngineKeys = new Dictionary<string, string> {
                {"hdr.convertToSdr","hdr.convSdr"},{"hdr.toneMapCurve","hdr.curve"},{"hdr.toggleDisplay","hdr.toggle"},{"hdr.maxNits","hdr.dispNits"},
                {"hdr.outputGamut","hdr.gamut"},{"hdr.osdBright","hdr.osdBrt"},{"hdr.exposureTrim","hdr.expTrim"},{"hdr.shadowLift","hdr.shLift"},
                {"hdr.hdrPass","hdr.raw"},{"postfx.debandThresh","postfx.dbThresh"},{"postfx.debandRange","postfx.dbRange"},
                {"postfx.debandIter","postfx.dbIter"},{"postfx.debandGrain","postfx.dbGrain"},{"zion.enabled","ml.enable"},{"zion.toneMode","ml.mode"},
                {"zion.shadowToe","ml.shadow"},{"zion.shadowDehaze","ml.shDehaze"},{"zion.gamma","ml.gamma"},{"zion.highlight","ml.highlight"},
                {"zion.highlightRolloffCtrl","ml.hiRoll"},{"zion.gammaTopUp","ml.gammaTopUp"},{"zion.tgtMid","ml.tgtMid"},{"zion.ctl","ml.ctl"},
                {"zion.ctlWhite","ml.ctlWhite"},{"zion.chN","ml.chN"},{"zion.chF","ml.chF"},{"zion.knee","ml.knee"},{"zion.skin","ml.skin"},
                {"zion.contrast","ml.contrastEnable"},{"zion.contrastStrength","ml.contrast"},{"zion.contrastZoneLo","ml.csZoneLo"},
                {"zion.contrastRatioCap","ml.csCap"},{"zion.contrastSkinProtect","ml.csSkin"},{"zion.contrastDebugVis","ml.contrastVis"},
                {"zion.barsRepaint","ml.bars"},{"zion.barsHighlight","ml.barsHighlight"},{"zion.debugCopyShaders","ml.debugCopy"},
                {"zion.debugFrameLog","ml.debugLog"},{"zion.skyEnabled","ml.sky"},{"zion.skyStrength","ml.skyStr"},{"zion.skyOverlay","ml.skyVis"},
                {"zion.personEnabled","ml.person"},{"zion.personStrength","ml.persStr"},{"zion.personOverlay","ml.personOv"},
                {"zion.skinPreserve","ml.skinPres"},{"out.useD3D11","out.d3d11"},{"out.texFormat","out.tex"},{"out.vpNV12","out.nv12"},
                {"out.vpP01x","out.p010"},{"out.vpYUY2","out.yuy2"},{"out.vpOther","out.other"},{"out.deinterlace","out.deint"},
                {"out.deintDouble","out.deintDb"},{"out.deintBlend","out.deintBl"},{"out.chromaScaling","out.chroma"},{"out.upscaling","out.upsc"},
                {"out.downscaling","out.downsc"},{"out.useDither","out.dither"},{"out.vBlank","out.vblank"},{"out.adjustPresent","out.adjust"},
                {"out.reinitDisplay","out.reinit"},{"out.swapEffect","out.swapEffect"},{"sys.trayIcon","out.tray"}
            };
            static readonly string[] Hundredths = { "geo.zoom", "geo.panX", "geo.panY", "geo.nlsStrength", "geo.nlsCenter", "geo.nlsSplit", "geo.nlsBars", "geo.nlsMax", "geo.nlsCropX", "geo.nlsCropY" };
            static readonly int[] AllCurves = { 1, 2, 3, 4, 5, 7, 8, 9, 10, 11 };
            static readonly string[] DeviceRows = { "hdr.sdrNits", "hdr.maxNits", "hdr.blackNits", "hdr.convertToSdr", "hdr.outputGamut" };

            static string EngineKey(string k) { string e; return EngineKeys.TryGetValue(k, out e) ? e : k; }
            static bool IsSettingsKey(string ek)
            {
                if (ek == "out.cubeLut" || ek == "out.cubeLutMode") return false;
                return ek.StartsWith("hdr.") || ek.StartsWith("ml.") || ek.StartsWith("out.") || ek.StartsWith("geo.") || ek.StartsWith("postfx.");
            }
            static bool IniBool(string v)
            {
                v = v.Trim();
                return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
            static double Num(string v, double def)
            {
                double d;
                return double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
            }
            static int Int(string v, int def)
            {
                int i;
                return int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? i : def;
            }

            // 2.64.0: the player's own picture settings. The first time, Unplugged's are copied
            // (Picture > Copy from Unplugged copies them again); after that Unplugged is not read.
            public static string UnpluggedIni { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unplugged", "s3_shell.ini"); } }
            public static string OwnIni { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "player_picture.ini"); } }

            static void EnsureOwn()
            {
                if (File.Exists(OwnIni)) return;
                if (!ImportFromUnplugged())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OwnIni));
                    File.WriteAllText(OwnIni, "# ClipGuard+ Player picture settings\r\n");
                }
            }

            public static bool ImportFromUnplugged()
            {
                try
                {
                    if (!File.Exists(UnpluggedIni)) return false;
                    Directory.CreateDirectory(Path.GetDirectoryName(OwnIni));
                    File.Copy(UnpluggedIni, OwnIni, true);
                    return true;
                }
                catch (Exception) { return false; }
            }

            // One value written to the player's own file (the line replaced, or added).
            public static void SetValue(string key, string val)
            {
                try
                {
                    EnsureOwn();
                    List<string> lines = new List<string>(File.ReadAllLines(OwnIni));
                    bool done = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string t = lines[i].Trim();
                        int eq = t.IndexOf('=');
                        if (eq > 0 && t.Substring(0, eq).Trim() == key) { lines[i] = key + "=" + val; done = true; break; }
                    }
                    if (!done) lines.Add(key + "=" + val);
                    File.WriteAllLines(OwnIni, lines.ToArray());
                }
                catch (Exception) { }
            }

            // ---- 2.65.0: the Picture tab: Unplugged's own settings, by section ----
            // Names, sections, ranges and steps follow Unplugged's menu (native_ui.cpp g_rows).
            // Left out: the video-processor rows (always off in the player), Direct3D 11
            // (always on), Unplugged's debug views and its app / source rows.
            // Keys starting with "!" are the player's own rows (handled by the player).
            public sealed class PRow
            {
                public int Sec; public string Key, Label, Help; public string[] Names; public double Step; public string Unit; public bool Gt7Only;
                public PRow(int sec, string key, string label, string[] names, double step, string unit, string help)
                { Sec = sec; Key = key; Label = label; Names = names; Step = step; Unit = unit; Help = help; }
                public PRow Gt7() { Gt7Only = true; return this; }
            }
            public static readonly string[] Sections = { "HDR & Tone Mapping", "Curve Settings", "Zion ML Grade", "Enhancement", "Scaling",
                "Geometry", "Non-Linear Stretch", "Calibration LUT", "Display & Output", "General" };
            static readonly string[] SectionHelp = {
                "Convert to SDR, tone curve, display target, exposure, gamut.",
                "The tone curve's own settings (GT7 Adapt has them; the Unplugged curve has none).",
                "Unplugged's machine-learning grade: tone curve, chroma, micro contrast, sky and skin.",
                "Sharpen and deband.",
                "Upscaler (FSRCNNX), downscaler, chroma, deinterlacing, processing format, dither.",
                "Fit mode, fixed aspect, zoom and pan.",
                "Non-linear stretch: fills a 2.39 picture onto the screen, for your anamorphic setup.",
                "Calibrate a projector, and the LUT the picture uses.",
                "Frame rate matching and how frames are presented.",
                "What the player opens in; copy the settings from Unplugged again." };
            const int S_HDR = 0, S_CURVE = 1, S_ZION = 2, S_ENH = 3, S_SCALE = 4, S_GEO = 5, S_NLS = 6, S_LUT = 7, S_OUT = 8, S_GEN = 9;
            public static readonly PRow[] PRows = {
                new PRow(S_HDR, "hdr.convertToSdr", "Convert to SDR", null, 0, "", "On: HDR is tone mapped to SDR for the projector. Off: HDR is sent as HDR."),
                new PRow(S_HDR, "hdr.toneMapCurve", "Tone curve", new[] { "Unplugged", "GT7 Adapt" }, 0, "", "The HDR to SDR curve. GT7 Adapt has its own settings under Curve Settings."),
                new PRow(S_HDR, "hdr.sdrNits", "SDR display nits", null, 1, " nits", "The brightness SDR white is mapped to."),
                new PRow(S_HDR, "hdr.exposureTrim", "Exposure", null, 1, "", "Overall HDR brightness trim."),
                new PRow(S_HDR, "hdr.shadowLift", "Shadow detail", null, 1, "", "Lifts or deepens the dark parts of HDR."),
                new PRow(S_HDR, "hdr.punch", "Contrast", null, 1, "", "Mid-tone contrast for HDR."),
                new PRow(S_HDR, "hdr.outputGamut", "Output gamut", new[] { "BT.709 (standard)", "BT.2020 (wide)" }, 0, "", "The colour gamut sent to the projector."),
                new PRow(S_HDR, "hdr.gamutCap", "Gamut capacity (709)", null, 0.01, "", "How much of BT.709 the gamut mapping may use."),
                new PRow(S_HDR, "hdr.hiDesat", "Highlight desat", null, 0.01, "", "Desaturates the brightest highlights."),
                new PRow(S_HDR, "hdr.preferDoVi", "Prefer DV over PQ/HLG", null, 0, "", "On: Dolby Vision metadata is used when the file has it (P5, P8, P7 MEL)."),
                new PRow(S_HDR, "hdr.toggleDisplay", "Windows HDR", new[] { "Do not change", "On (fullscreen)", "On", "On/off (fullscreen)", "On/off" }, 0, "", "Whether Windows HDR is switched for HDR sources."),
                new PRow(S_HDR, "hdr.maxNits", "Static nits (max)", null, 5, " nits", "The brightest the projector shows (tone mapping target)."),
                // 2.70.0: for every curve (was GT7 Adapt only): capture never carries HDR metadata.
                new PRow(S_HDR, "hdr.titlePeak", "Title peak (nits)", null, 1, " nits", "The signal's peak when it has no HDR metadata: always with capture, and some files. MaxCLL wins when a film has it."),
                new PRow(S_HDR, "hdr.blackNits", "Display black (nits)", null, 0.001, " nits", "The projector's black level for tone mapping."),
                new PRow(S_HDR, "hdr.osdBright", "Subtitle/OSD brightness", null, 1, "", "The brightness of subtitles and on-screen text in HDR."),
                new PRow(S_CURVE, "hdr.softKnee", "Soft knee (0=off)", null, 0.01, "", "The curve's soft knee.").Gt7(),
                new PRow(S_CURVE, "hdr.softKneeRatio", "Soft knee ratio", null, 0.01, "", "The soft knee's ratio.").Gt7(),
                new PRow(S_CURVE, "hdr.softKneeWidth", "Soft knee width", null, 0.01, "", "The soft knee's width.").Gt7(),
                new PRow(S_CURVE, "hdr.autoRefW", "Auto exposure", null, 0, "", "Sets the exposure from the scene.").Gt7(),
                new PRow(S_CURVE, "hdr.refWTgt", "Exposure target", null, 0.01, "", "The auto exposure's target.").Gt7(),
                new PRow(S_CURVE, "hdr.gtBlend", "GT7 blend (ch<->UCS)", null, 0.01, "", "GT7's blend between per-channel and UCS.").Gt7(),
                new PRow(S_CURVE, "hdr.gtFade", "GT7 chroma fade from", null, 0.01, "", "Where GT7 starts fading chroma.").Gt7(),
                new PRow(S_CURVE, "hdr.gtAdapt", "GT7 adapt to scene peak", null, 0.01, "", "How much GT7 adapts to the scene's peak.").Gt7(),
                new PRow(S_CURVE, "hdr.gtAlpha", "GT7 shoulder reach", null, 0.01, "", "GT7's shoulder reach.").Gt7(),
                new PRow(S_CURVE, "hdr.gtDesat", "GT7 desaturation", null, 0.01, "", "GT7's highlight desaturation.").Gt7(),
                new PRow(S_ZION, "zion.enabled", "Enable ML grade", null, 0, "", "Unplugged's machine-learning grade for HDR."),
                new PRow(S_ZION, "zion.ctl", "Band controller", null, 0, "", "Tone curve: the band controller."),
                new PRow(S_ZION, "zion.toneMode", "Tone mode", new[] { "Auto", "Manual", "Off" }, 0, "", "Tone curve: Auto, Manual (the rows below) or Off."),
                new PRow(S_ZION, "zion.shadowToe", "Shadow", null, 0.01, "", "Tone curve: shadow toe."),
                new PRow(S_ZION, "zion.shadowDehaze", "Shadow dehaze", null, 0.01, "", "Tone curve: shadow dehaze."),
                new PRow(S_ZION, "zion.gamma", "Gamma", null, 0.01, "", "Tone curve: gamma."),
                new PRow(S_ZION, "zion.gammaTopUp", "Gamma top-up", null, 0.01, "", "Tone curve: gamma top-up."),
                new PRow(S_ZION, "zion.tgtMid", "Mid-tone target", null, 0.01, "", "Tone curve: mid-tone target (0 = auto)."),
                new PRow(S_ZION, "zion.highlight", "Highlight", null, 0.01, "", "Tone curve: highlight."),
                new PRow(S_ZION, "zion.highlightRolloffCtrl", "Highlight shoulder", null, 0.01, "", "Tone curve: highlight shoulder."),
                new PRow(S_ZION, "zion.chN", "Chroma normal", null, 0.01, "", "Chroma: normal."),
                new PRow(S_ZION, "zion.chF", "Chroma fire", null, 0.01, "", "Chroma: fire."),
                new PRow(S_ZION, "zion.knee", "Chroma knee", null, 0.01, "", "Chroma: knee."),
                new PRow(S_ZION, "zion.skin", "Chroma skin", null, 0.01, "", "Chroma: skin."),
                new PRow(S_ZION, "zion.contrast", "Micro contrast", null, 0, "", "Micro contrast on or off."),
                new PRow(S_ZION, "zion.contrastStrength", "Micro contrast strength", null, 0.01, "", "Micro contrast: strength."),
                new PRow(S_ZION, "zion.contrastZoneLo", "Micro contrast zone lo", null, 0.01, "", "Micro contrast: zone low."),
                new PRow(S_ZION, "zion.contrastRatioCap", "Micro contrast ratio cap", null, 0.01, "", "Micro contrast: ratio cap."),
                new PRow(S_ZION, "zion.contrastSkinProtect", "Micro contrast skin protect", null, 0.01, "", "Micro contrast: skin protect."),
                new PRow(S_ZION, "zion.skyEnabled", "Sky", null, 0, "", "Sky segmentation on or off."),
                new PRow(S_ZION, "zion.skyStrength", "Sky strength", null, 0.01, "", "Sky segmentation strength."),
                new PRow(S_ZION, "zion.personEnabled", "Person skin", null, 0, "", "Person skin on or off."),
                new PRow(S_ZION, "zion.personStrength", "Person skin strength", null, 0.01, "", "Person skin strength."),
                new PRow(S_ZION, "zion.skinPreserve", "Skin tone preserve", null, 0.01, "", "Person skin: tone preserve."),
                new PRow(S_ENH, "postfx.sharpen", "Adaptive sharpen", null, 0, "", "Adaptive sharpening on or off."),
                new PRow(S_ENH, "postfx.sharpenStr", "Sharpen strength", null, 0.01, "", "How strong the sharpening is."),
                new PRow(S_ENH, "postfx.deband", "Deband", null, 0, "", "Smooths banding in gradients."),
                new PRow(S_ENH, "postfx.debandThresh", "Deband threshold", null, 1, "", "Deband: threshold."),
                new PRow(S_ENH, "postfx.debandRange", "Deband range", null, 1, "", "Deband: range."),
                new PRow(S_ENH, "postfx.debandIter", "Deband iterations", null, 1, "", "Deband: iterations."),
                new PRow(S_ENH, "postfx.debandGrain", "Deband grain", null, 1, "", "Deband: grain."),
                new PRow(S_SCALE, "out.upscaling", "Upscaling", new[] { "Lanczos3", "Jinc2", "FSRCNNX" }, 0, "", "FSRCNNX is ClipGuard's neural upscaler for about 2x (1080p on 4K); other sizes use Jinc2. Also in F7."),
                new PRow(S_SCALE, "out.downscaling", "Downscaling", new[] { "Lanczos", "BicubicSharp" }, 0, "", "How the picture is downscaled."),
                new PRow(S_SCALE, "out.chromaScaling", "Chroma upsampling", new[] { "CatmullRom", "KrigBilateral" }, 0, "", "How the colour (chroma) is upscaled."),
                new PRow(S_SCALE, "out.superRes", "Super Resolution", null, 0, "", "The graphics card's video super resolution."),
                new PRow(S_SCALE, "out.rtxVideoHdr", "RTX Video HDR", null, 0, "", "NVIDIA RTX Video HDR (SDR to HDR)."),
                new PRow(S_SCALE, "out.deinterlace", "Deinterlacing", new[] { "Disable", "Enable", "Hack future frames" }, 0, "", "For interlaced sources (1080i)."),
                new PRow(S_SCALE, "out.deintDouble", "Double frame rate", null, 0, "", "Deinterlaces to the field rate (1080i60 to 60 frames)."),
                new PRow(S_SCALE, "out.deintBlend", "Blend deint 4:2:0", null, 0, "", "Blend deinterlacing for 4:2:0."),
                new PRow(S_SCALE, "out.texFormat", "Texture format", new[] { "Auto", "8-bit", "10-bit", "16-bit float" }, 0, "", "The precision the picture is processed in."),
                new PRow(S_SCALE, "out.useDither", "Use dithering", null, 0, "", "Dithers the output to the display's bit depth."),
                new PRow(S_GEO, "geo.mode", "Fit mode", new[] { "Source", "Fixed aspect", "Fill screen" }, 0, "", "How the picture is fitted to the screen."),
                new PRow(S_GEO, "geo.aspect", "Fixed aspect", null, 0.01, "", "The aspect for Fit mode = Fixed aspect."),
                new PRow(S_GEO, "geo.zoom", "Zoom", null, 1, " %", "Zoom."),
                new PRow(S_GEO, "geo.panX", "Pan X", null, 1, "", "Pan sideways."),
                new PRow(S_GEO, "geo.panY", "Pan Y", null, 1, "", "Pan up and down."),
                new PRow(S_NLS, "geo.nls", "Enable NLS", null, 0, "", "Non-linear stretch on or off."),
                new PRow(S_NLS, "geo.nlsBars", "Bars removed", null, 1, " %", "How much of the letterbox the fill takes (100 fills the screen)."),
                new PRow(S_NLS, "geo.nlsAxis", "Axis", new[] { "Horizontal", "Vertical", "Split" }, 0, "", "Which axis carries the correction. Horizontal never stretches height."),
                new PRow(S_NLS, "geo.nlsStrength", "Strength", null, 1, " %", "NLS strength."),
                new PRow(S_NLS, "geo.nlsCenter", "Centre protection", null, 1, " %", "How much of the centre stays undistorted."),
                new PRow(S_NLS, "geo.nlsCurve", "Curve", null, 0.05, "", "How the stretch grows towards the edges."),
                new PRow(S_NLS, "geo.nlsSplit", "Split", null, 1, " %", "With Axis = Split: the horizontal share of the correction."),
                new PRow(S_NLS, "geo.nlsMax", "Max distortion", null, 1, " %", "The most the picture may be distorted."),
                new PRow(S_NLS, "geo.nlsCropX", "Post NLS X-Crop", null, 1, " %", "Crop after the stretch, sideways."),
                new PRow(S_NLS, "geo.nlsCropY", "Post NLS Y-Crop", null, 1, " %", "Crop after the stretch, up and down."),
                new PRow(S_NLS, "geo.nlsFillMode", "Fill mode", new[] { "Auto", "Stretch", "Crop", "Off" }, 0, "", "What to do when the picture must be fitted."),
                new PRow(S_NLS, "geo.nlsLock", "Lock band", null, 0, "", "Keeps the detected black band from moving."),
                new PRow(S_LUT, "!calrun", "Calibrate a projector...", null, 0, "", "Measures the projector with the meter and builds a 3D LUT around your white, with a check and a PDF report. Enter starts: the projector's name, then its .ccss correction or none."),
                new PRow(S_LUT, "!lut", "LUT", null, 0, "", "The calibration LUT the picture uses: None, or a projector you calibrated. F5 while watching turns it off and on for comparing."),
                new PRow(S_LUT, "out.cubeLutMode", "LUT mode", new[] { "Off", "Gamma only", "Greyscale", "Full 3D" }, 0, "", "How the calibration LUT is applied (Full 3D for a calibration)."),
                new PRow(S_LUT, "!caldir", "Calibration folder", null, 0, "", "Put a projector's .ccss here, named after the projector (for example Epson LS12000.ccss). The LUTs and reports are saved here too."),
                new PRow(S_OUT, "!frm", "Match frame rate", null, 0, "", "Switches the graphics card's refresh rate to the film's or the capture source's (23.976, 24, 50, 59.94, 60) and back in the library."),
                new PRow(S_OUT, "out.interpolate50", "Interpolate at 50%", null, 0, "", "Frame interpolation at 50 %."),
                new PRow(S_OUT, "out.swapEffect", "Swap effect", new[] { "Discard", "Flip" }, 0, "", "How frames are handed to Windows."),
                new PRow(S_OUT, "out.exclusiveFS", "Exclusive fullscreen", null, 0, "", "Exclusive fullscreen."),
                new PRow(S_OUT, "out.vBlank", "Wait for VBlank", null, 0, "", "Waits for the display's vertical blank."),
                new PRow(S_OUT, "out.adjustPresent", "Adjust present time", null, 0, "", "Adjusts when frames are presented."),
                new PRow(S_OUT, "out.reinitDisplay", "Reinit on display change", null, 0, "", "Rebuilds the output when the display changes."),
                new PRow(S_OUT, "out.showStats", "Show statistics", null, 0, "", "The renderer's statistics on the picture."),
                new PRow(S_GEN, "!startin", "Start in", null, 0, "", "What the player opens in: the library, or the capture card's picture (the library is then Back / F4 away)."),
                new PRow(S_GEN, "!copy", "Copy from Unplugged", null, 0, "", "Replaces these settings with Unplugged's current ones (they were copied once, when the player first started)."),
            };

            public static string SectionHelpOf(int sec) { return sec >= 0 && sec < SectionHelp.Length ? SectionHelp[sec] : ""; }

            static R Find(List<R> rows, string key) { foreach (R r in rows) if (r.Key == key) return r; return null; }

            static bool Gt7Selected(List<R> rows)
            {
                R c = Find(rows, "hdr.toneMapCurve");
                return c != null && c.OptVals != null && c.Opt >= 0 && c.Opt < c.OptVals.Length && c.OptVals[c.Opt] == 10;
            }

            // The rows a section shows now (Curve Settings only for GT7 Adapt).
            public static List<int> SectionRows(int sec)
            {
                List<R> rows;
                try { rows = Load(); } catch (Exception) { rows = new List<R>(); }
                bool gt7 = Gt7Selected(rows);
                List<int> l = new List<int>();
                for (int i = 0; i < PRows.Length; i++) if (PRows[i].Sec == sec && (!PRows[i].Gt7Only || gt7)) l.Add(i);
                return l;
            }

            static string Show(R r, PRow p)
            {
                if (r == null) return "-";
                if (r.Kind == 4) return string.IsNullOrEmpty(r.File) ? "None" : Path.GetFileName(r.File);
                if (r.Kind == 0) return r.On ? "On" : "Off";
                if (r.Key == "out.upscaling" && GetFsr != null && GetFsr()) return "FSRCNNX";
                if (r.Kind == 2) return p.Names != null && r.Opt >= 0 && r.Opt < p.Names.Length ? p.Names[r.Opt] : r.Opt.ToString(CultureInfo.InvariantCulture);
                return r.Val.ToString(p.Step < 0.01 ? "0.000" : (p.Step < 0.1 ? "0.00" : (p.Step < 1 ? "0.0" : "0")), CultureInfo.InvariantCulture) + p.Unit;
            }

            // A row's value as shown ("" for the player's own rows).
            public static string ValueOf(int i)
            {
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return "-"; }
                PRow p = PRows[i];
                if (p.Key.StartsWith("!")) return "";
                return Show(Find(rows, p.Key), p);
            }

            public static Func<bool> GetFsr;
            public static Action<bool> SetFsr;

            public static void PictureStep(int i, int dir)
            {
                if (i < 0 || i >= PRows.Length) return;
                PRow p = PRows[i];
                if (p.Key.StartsWith("!")) return;
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return; }
                R r = Find(rows, p.Key);
                if (r == null || r.Kind == 4) return;
                if (r.Kind == 0) SetValue(r.Key, r.On ? "false" : "true");
                else if (r.Kind == 1)
                {
                    double v = Math.Max(r.Mn, Math.Min(r.Mx, Math.Round((r.Val + dir * p.Step) / p.Step) * p.Step));
                    SetValue(r.Key, v.ToString("G6", CultureInfo.InvariantCulture));
                }
                else if (r.Key == "out.upscaling" && SetFsr != null)
                {
                    // Lanczos3, Jinc2, FSRCNNX (ClipGuard's, with Jinc2 for other sizes).
                    int cur = (GetFsr != null && GetFsr()) ? 2 : r.Opt;
                    int o = ((cur + dir) % 3 + 3) % 3;
                    SetValue(r.Key, (o == 0 ? r.OptVals[0] : r.OptVals[1]).ToString(CultureInfo.InvariantCulture));
                    SetFsr(o == 2);
                }
                else if (r.Kind == 2 && r.NOpts > 0)
                {
                    int o = ((r.Opt + dir) % r.NOpts + r.NOpts) % r.NOpts;
                    int val = r.OptVals != null ? r.OptVals[o] : o;
                    SetValue(r.Key, val.ToString(CultureInfo.InvariantCulture));
                    if (r.Key == "hdr.toneMapCurve") SetValue("ui.curveValueIsEngine", "true");
                }
            }

            static List<R> Load()
            {
                Dictionary<string, string> ini = new Dictionary<string, string>();
                try
                {
                    EnsureOwn();
                    foreach (string raw in File.ReadAllLines(OwnIni))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        ini[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
                catch (Exception) { }
                bool curveEngine = ini.ContainsKey("ui.curveValueIsEngine") && IniBool(ini["ui.curveValueIsEngine"]);
                List<R> rows = new List<R>();
                foreach (R d in Rows)
                {
                    R r = d.Copy();
                    string v;
                    if (ini.TryGetValue(r.Key, out v))
                    {
                        if (r.Kind == 0)
                        {
                            r.On = IniBool(v);
                            if (r.Key == "out.superRes" && Int(v, 0) != 0) r.On = true;
                        }
                        else if (r.Kind == 4) r.File = v;
                        else if (r.Kind == 1) r.Val = Num(v, r.Val);
                        else if (r.Kind == 2)
                        {
                            if (r.Key == "hdr.toneMapCurve")
                            {
                                int raw = Int(v, 0), engine = 0;
                                if (curveEngine) { foreach (int cv in AllCurves) if (raw == cv) engine = raw; }
                                else if (raw >= 0 && raw < AllCurves.Length) engine = AllCurves[raw];
                                else { foreach (int cv in AllCurves) if (raw == cv) engine = raw; }
                                r.Opt = 0;
                                for (int j = 0; j < r.OptVals.Length; j++) if (r.OptVals[j] == engine) r.Opt = j;
                            }
                            else
                            {
                                int o = Int(v, -1);
                                if (r.OptVals != null) { for (int j = 0; j < r.NOpts; j++) if (r.OptVals[j] == o) r.Opt = j; }
                                else if (o >= 0 && o < r.NOpts) r.Opt = o;
                            }
                        }
                    }
                    rows.Add(r);
                }
                // 2.55.0: no preset file is laid over these: Unplugged starts from s3_shell.ini
                // too (a preset's file is applied only when one is picked, and edits keep
                // s3_shell.ini current), so its saved values are exactly what Unplugged shows.
                foreach (R r in rows)
                {
                    // 2.63.3: the graphics card's video processor is never used in the player (it
                    // would replace the renderer's chroma upscaling and shader path)...
                    if (r.Key == "out.vpNV12" || r.Key == "out.vpP01x" || r.Key == "out.vpYUY2" || r.Key == "out.vpOther")
                        r.On = false;
                    // ...and the sound is always bitstreamed (Dolby / DTS to the receiver as they
                    // are), through MPC-BE's MPC Audio Renderer in WASAPI exclusive mode.
                    if (r.Key == "audio.mode") r.Opt = 0;   // option 0 = Bitstream (value 1)
                }
                return rows;
            }

            // 2.66.0: the calibration LUT (the renderer's own 3D LUT, set by the player).
            public static string LutFile()
            {
                try { foreach (R r in Load()) if (r.Key == "out.cubeLut") return r.File ?? ""; } catch (Exception) { }
                return "";
            }
            public static void SetLut(string file)
            {
                SetValue("out.cubeLut", file ?? "");
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return; }
                R m = Find(rows, "out.cubeLutMode");
                if (m == null) return;
                if (string.IsNullOrEmpty(file)) SetValue("out.cubeLutMode", "0");
                else if (m.Opt == 0) SetValue("out.cubeLutMode", "3");
            }
            public static void PushLut()
            {
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return; }
                R lutPath = Find(rows, "out.cubeLut"), lutMode = Find(rows, "out.cubeLutMode");
                if (lutPath == null || lutMode == null) return;
                int m = lutMode.Opt;
                string file = m > 0 ? lutPath.File : "";
                Engine.Cmd("{\"a\":\"lut\",\"file\":\"" + Engine.Esc(file) + "\",\"mode\":" + m.ToString(CultureInfo.InvariantCulture) + "}");
            }

            public static void Push()
            {
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return; }
                StringBuilder sb = new StringBuilder();
                R lutPath = null, lutMode = null;
                foreach (R r in rows)
                {
                    if (r.Key == "out.cubeLut") lutPath = r;
                    if (r.Key == "out.cubeLutMode") lutMode = r;
                    if (r.NoEng != 0) continue;
                    if (r.Kind != 0 && r.Kind != 1 && r.Kind != 2) continue;
                    string ek = EngineKey(r.Key);
                    if (!IsSettingsKey(ek)) continue;
                    string v;
                    if (r.Kind == 0 && r.Key == "out.superRes") v = r.On ? "4" : "0";
                    else if (r.Kind == 0) v = r.On ? "true" : "false";
                    else if (r.Kind == 1) v = (Array.IndexOf(Hundredths, r.Key) >= 0 ? r.Val / 100.0 : r.Val).ToString("G6", CultureInfo.InvariantCulture);
                    else v = (r.OptVals != null ? r.OptVals[r.Opt] : r.Opt).ToString(CultureInfo.InvariantCulture);
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(ek).Append('=').Append(v);
                }
                if (sb.Length > 0) Engine.Cmd("{\"a\":\"set\",\"sets\":\"" + sb + "\"}");
                if (lutPath != null && lutMode != null)
                {
                    int m = lutMode.Opt;
                    string file = m > 0 ? lutPath.File : "";
                    Engine.Cmd("{\"a\":\"lut\",\"file\":\"" + Engine.Esc(file) + "\",\"mode\":" + m.ToString(CultureInfo.InvariantCulture) + "}");
                }
            }
        }

#if PLAYER
        // 2.70.0: a black screen behind the player for as long as it runs, so a switch between
        // the library and the picture never shows the Windows desktop or taskbar. It never
        // takes the focus (and gives it back to the player if clicked).
        sealed class Backdrop : Form
        {
            readonly Form owner;
            public Backdrop(Form owner, Rectangle bounds)
            {
                this.owner = owner;
                Text = "ClipGuard Media Player backdrop";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
                BackColor = Color.Black;
                TopMost = true;
                ShowInTaskbar = false;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; }   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            }
            protected override void OnActivated(EventArgs e)
            {
                base.OnActivated(e);
                try { if (owner.Visible) owner.Activate(); } catch (Exception) { }
            }
        }
        Backdrop backdrop;
#endif

        sealed class VideoForm : Form
        {
            readonly MediaPlayer owner;
            public readonly Panel Host;
            public VideoForm(MediaPlayer owner, Rectangle bounds)
            {
                this.owner = owner;
                Text = "ClipGuard+ Player";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
                BackColor = Color.Black;
                TopMost = true;
                KeyPreview = true;
                ShowInTaskbar = false;
                // 2.56.0: the picture fills the whole screen, so a 3840 x 2160 film is shown
                // 1:1 with no scaling. (2.53.1 kept it 6 px inside the screen, the way
                // Unplugged's host sits; the renderer's full-screen flag that avoided only
                // matters with the HDR display toggle, and the choppiness was CPU decoding.)
                Host = new Panel();
                Host.BackColor = Color.Black;
                Host.Bounds = new Rectangle(0, 0, Math.Max(16, bounds.Width), Math.Max(16, bounds.Height));
                Controls.Add(Host);
            }
            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; owner.Quit(); return; }
                base.OnFormClosing(e);
            }
            // 2.56.0: every key on the picture goes to the player's controls first (arrows
            // included, which a form would otherwise use to move between controls).
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                keyData = owner.MapKey(keyData);   // 2.63.0: learned remote buttons
                if ((keyData & Keys.Alt) == 0 && owner.PlayerKey(keyData & Keys.KeyCode, (keyData & Keys.Shift) != 0, (keyData & Keys.Control) != 0))
                    return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
            // 2.57.0: remote buttons sent as WM_APPCOMMAND (play, pause, stop, skip, back).
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0319)
                {
                    int cmd = (int)(((long)m.LParam >> 16) & 0x0FFF);
                    Keys t = owner.MapApp(cmd);   // 2.63.0: a learned remote button first
                    if (t != Keys.None && owner.PlayerKey(t & Keys.KeyCode, (t & Keys.Shift) != 0, (t & Keys.Control) != 0)) { m.Result = (IntPtr)1; return; }
                    if (owner.PlayerAppCommand(cmd)) { m.Result = (IntPtr)1; return; }
                }
                base.WndProc(ref m);
            }
            protected override void OnResize(EventArgs e) { base.OnResize(e); if (Engine.Attached) Engine.Cmd("{\"a\":\"resize\"}"); }
            protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Color.Black); }
        }

        static class Engine
        {
            [DllImport("s3engine.dll", CallingConvention = CallingConvention.Cdecl)] static extern int s3_attach(long hwnd);
            [DllImport("s3engine.dll", CallingConvention = CallingConvention.Cdecl)] static extern int s3_cmd(byte[] jsonUtf8);
            [DllImport("s3engine.dll", CallingConvention = CallingConvention.Cdecl)] static extern int s3_start_device(byte[] deviceUtf8);
            // 2.64.0: DeckLink capture by the card's name; "" stops it.
            public static int StartDevice(string name) { return s3_start_device(Encoding.UTF8.GetBytes((name ?? "") + "\0")); }
            [DllImport("s3engine.dll", CallingConvention = CallingConvention.Cdecl)] static extern int s3_stats(byte[] buf, int cap);
            [DllImport("s3engine.dll", CallingConvention = CallingConvention.Cdecl)] static extern void s3_shutdown();

            public static bool Attached;
            public static void Attach(IntPtr hwnd) { s3_attach(hwnd.ToInt64()); Attached = true; }

            public static int Cmd(string json)
            {
                byte[] b = Encoding.UTF8.GetBytes(json + "\0");
                return s3_cmd(b);
            }

            public static string State() { int g; long p, d; return State(out g, out p, out d); }

            // 2.56.0: the whole status (track lists included).
            public static string Stats()
            {
                byte[] buf = new byte[65536];
                int n = s3_stats(buf, buf.Length);
                return n > 0 ? Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length)) : "";
            }

            // pos / dur in 100 ns units (-1 when not known).
            public static string State(out int gen, out long pos, out long dur)
            {
                gen = -1; pos = -1; dur = -1;
                byte[] buf = new byte[16384];
                int n = s3_stats(buf, buf.Length);
                if (n <= 0) return "";
                string j = Encoding.UTF8.GetString(buf, 0, n);
                Match g = Regex.Match(j, "\"gen\":(\\d+)");
                if (g.Success) int.TryParse(g.Groups[1].Value, out gen);
                Match pm = Regex.Match(j, "\"pos\":(-?\\d+)");
                if (pm.Success) long.TryParse(pm.Groups[1].Value, out pos);
                Match dm = Regex.Match(j, "\"dur\":(-?\\d+)");
                if (dm.Success) long.TryParse(dm.Groups[1].Value, out dur);
                Match m = Regex.Match(j, "\"state\":\"([a-z]+)\"");
                return m.Success ? m.Groups[1].Value : "";
            }

            public static string Esc(string v) { return v.Replace("\\", "\\\\").Replace("\"", "\\\""); }

            public static void Shutdown() { if (Attached) { Attached = false; try { s3_shutdown(); } catch (Exception) { } } }
        }
#endif

        void Timeline(Movie m, string state)
        {
            if (m == null || lastPos < 0) return;
            try
            {
                Plex.Http("GET", Url(m.Srv, "/:/timeline?ratingKey=" + m.Key + "&key=%2Flibrary%2Fmetadata%2F" + m.Key + "&state=" + state
                    + "&time=" + lastPos.ToString(CultureInfo.InvariantCulture) + "&duration=" + Math.Max(lastDur, m.Duration).ToString(CultureInfo.InvariantCulture)), m.Srv.Token, 8000);
            }
            catch (Exception) { }
        }

#if !PLAYER
        // F4 / Esc / Enter (lip sync): stop here and go back to the library.
        void StopFilm(bool saveLip)
        {
            if (player == null) return;
            playTimer.Stop();
            if (lipSync && saveLip)
            {
                unpluggedDelay = ClampDelay(TotalDelay() + lipDelta - LookAheadMs());
                SetIni(DelayKey, unpluggedDelay.ToString(CultureInfo.InvariantCulture));
            }
            Show();
            Activate();
            Busy("Stopping...");
            Bg2(delegate
            {
                try
                {
                    string h = WebGet("/variables.html", 1500);
                    long pos = Var(h, "position"), dur = Var(h, "duration");
                    if (pos >= 0 && dur > 0) { lastPos = pos; lastDur = dur; }
                }
                catch (Exception) { }
                CloseUnplugged();
            }, delegate { FilmEnded(); });
        }

#endif
        void FilmEnded()
        {
            playTimer.Stop();
#if PLAYER
            FrmRestore();   // 2.65.0: the desktop's refresh rate in the library
            frmResumeAt = DateTime.MaxValue;
#endif
            UnregisterHotKey(Handle, HK_F4);
            for (int id = HK_L; id <= HK_ESC; id++) UnregisterHotKey(Handle, id);
            Movie m = playing;
            bool lip = lipSync;
            player = null;
            playing = null;
            lipSync = false;
            if (!Visible) { Show(); Activate(); }
            if (lip)
            {
                scr = Screen2.Settings; tab = TabSettings; navFocus = false;
#if PLAYER
                setIdx = 1;   // Lip sync test (2.71.0: one row higher)
#else
                setIdx = 2;
#endif
                Invalidate();
                return;
            }
#if PLAYER
            // 2.57.0: back to the film's page (marked watched when it was seen to the end).
            if (m != null) { det = m; scr = Screen2.Details; detBtn = 0; detOnVersion = false; }
            else scr = detailsFrom == Screen2.Movies ? Screen2.Movies : Screen2.Home;
            // 2.68.0: an episode seen to the end: the next one, after a countdown.
            if (m != null && endedNat && m.Type == "episode" && Ini("NextEpisode", "1") != "0") NextUp(m);
            endedNat = false;
#else
            scr = detailsFrom == Screen2.Movies ? Screen2.Movies : Screen2.Home;
#endif
            tab = detailsFrom == Screen2.Movies ? 1 : ((detailsFrom == Screen2.Show || detailsFrom == Screen2.Shows) && TabTV >= 0 ? TabTV : 0);
            navFocus = false;
            Invalidate();
            if (m == null) return;
            long pos = lastPos, dur = Math.Max(lastDur, m.Duration);
            Bg2(delegate
            {
                bool done = pos < 0 && dur > 0 ? false : dur > 0 && pos >= dur * 0.9;
                if (pos < 0) pos = 0;
                Timeline(m, "stopped");
                if (done) Plex.Http("GET", Url(m.Srv, "/:/scrobble?identifier=com.plexapp.plugins.library&key=" + m.Key), m.Srv.Token, 10000);
                m.Offset = done ? 0 : pos;
                if (done) m.ViewCount = Math.Max(1, m.ViewCount);
            }, delegate { RefreshContinue(); Invalidate(); });
        }

#if !PLAYER
        // ---- leaving: Unplugged back to the Video Processor ------------------------------------
        bool exiting;
        public void ExitToProcessor()
        {
            if (exiting) return;
            exiting = true;
            if (pinTimer != null) pinTimer.Stop();
            Show();
            Busy("Back to Video Processor...");
            Movie m = playing;
            bool hadFilm = player != null;
            playTimer.Stop();
            UnregisterHotKey(Handle, HK_F4);
            for (int id = HK_L; id <= HK_ESC; id++) UnregisterHotKey(Handle, id);
            Bg2(delegate
            {
                if (hadFilm)
                {
                    try
                    {
                        string h = WebGet("/variables.html", 1500);
                        long pos = Var(h, "position"), dur = Var(h, "duration");
                        if (pos >= 0 && dur > 0) { lastPos = pos; lastDur = dur; }
                    }
                    catch (Exception) { }
                    if (m != null) Timeline(m, "stopped");
                }
                CloseUnplugged();
                RestoreUnplugged();
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(workRoot, "Unplugged.exe"));
                psi.WorkingDirectory = workRoot;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }, delegate { Close(); });
        }

#endif
        // Leave the host's way (F7 Source row, ClipGuard closing).
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true;
#if PLAYER
            try { if (backdrop != null) { backdrop.Close(); backdrop = null; } } catch (Exception) { }
#endif
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
            lock (imgLock) Monitor.PulseAll(imgLock);
            playTimer.Stop();
            if (pinTimer != null) pinTimer.Stop();
            UnregisterHotKey(Handle, HK_F4);
            for (int id = HK_L; id <= HK_ESC; id++) UnregisterHotKey(Handle, id);
            if (savedMode != null) { try { RestoreUnplugged(); } catch (Exception) { } }
#if PLAYER
            Quit();
#endif
            lock (imgLock) { foreach (Image im in imgs.Values) im.Dispose(); imgs.Clear(); }
            base.OnFormClosed(e);
            host.MediaClosed();
        }

        // ---- drawing (a 1920 x 1080 layout, scaled to the screen) -------------------------------
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float sx = ClientSize.Width / 1920f, sy = ClientSize.Height / 1080f, sc = Math.Min(sx, sy);
            g.TranslateTransform((ClientSize.Width - 1920 * sc) / 2, (ClientSize.Height - 1080 * sc) / 2);
            g.ScaleTransform(sc, sc);
            try
            {
                switch (scr)
                {
                    case Screen2.Busy: Center(g, busyText, 34, Color.White, 520); break;
                    case Screen2.SignIn: DrawSignIn(g); break;
                    case Screen2.Details: DrawDetails(g); break;
#if PLAYER
                    case Screen2.Show: DrawShowPage(g); break;
                    case Screen2.Starting: DrawStarting(g); break;
#endif
                    default:
                        DrawNav(g);
                        if (scr == Screen2.Home) DrawHome(g);
                        else if (scr == Screen2.Movies || scr == Screen2.Shows) DrawGrid(g);
#if PLAYER
                        else if (scr == Screen2.VP) DrawVP(g);
                        else if (scr == Screen2.Picture) DrawPicture(g);
                        else if (scr == Screen2.ClipGuard) DrawCgTab(g);
#endif
                        else DrawSettings(g);
                        break;
                }
                if (message.Length > 0) DrawMessage(g);
                if (learning) DrawLearn(g);
#if PLAYER
                if (calState != 0) DrawCalDlg(g);
#endif
            }
            catch (Exception) { }
        }

        static Font F(float size, bool bold) { return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }

        static void Txt(Graphics g, string s, float size, bool bold, Color c, float x, float y)
        {
            using (Font f = F(size, bold)) using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }

        static void TextBox(Graphics g, string s, float size, bool bold, Color c, RectangleF r)
        {
            using (Font f = F(size, bold)) using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisWord;
                g.DrawString(s, f, b, r, sf);
            }
        }

        static void Center(Graphics g, string s, float size, Color c, float y)
        {
            using (Font f = F(size, false)) using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                g.DrawString(s, f, b, new RectangleF(160, y, 1600, 400), sf);
            }
        }

        // The selection: yellow with a white outline.
        static void Focus(Graphics g, RectangleF r)
        {
            using (Pen w = new Pen(Color.White, 3)) g.DrawRectangle(w, r.X - 6, r.Y - 6, r.Width + 12, r.Height + 12);
            using (Pen y = new Pen(Yellow, 5)) g.DrawRectangle(y, r.X - 2.5f, r.Y - 2.5f, r.Width + 5, r.Height + 5);
        }

        void DrawNav(Graphics g)
        {
            Txt(g, AppName, 30, true, Color.White, 96, 40);
            string[] tabs = TabNames;
            float x = TabNames.Length > 6 ? 470 : 560;
            using (Font f = F(26, true))
                for (int i = 0; i < tabs.Length; i++)
                {
                    SizeF sz = g.MeasureString(tabs[i], f);
                    using (SolidBrush b = new SolidBrush(i == tab ? Color.White : Grey)) g.DrawString(tabs[i], f, b, x, 44);
                    if (i == tab) using (SolidBrush y = new SolidBrush(Yellow)) g.FillRectangle(y, x + 4, 84, sz.Width - 8, 4);
                    if (navFocus && i == tab) Focus(g, new RectangleF(x - 4, 44, sz.Width + 8, 40));
                    x += sz.Width + 40;
                }
            Txt(g, "Arrows: move    Enter: open    Esc: back    F4: Video Processor", 18, false, Grey, 96, 1036);
        }

        void Poster(Graphics g, Movie m, int idx, float x, float y, float w, float h, bool sel)
        {
            RectangleF r = new RectangleF(x, y, w, h);
            Image im = Img(m, m.Poster, w > 300 ? 720 : 440, w > 300 ? 1080 : 660);   // 2.52.1: sharp on a 4K screen; 2.68.0: an episode shows its show's poster
            if (im != null) g.DrawImage(im, r);
            else
            {
                using (SolidBrush b = new SolidBrush(Tones[Math.Abs(idx) % Tones.Length])) g.FillRectangle(b, r);
                TextBox(g, m.Title, 22, true, Color.White, new RectangleF(x + 14, y + h - 120, w - 28, 110));
            }
            if (m.Offset > 0 && m.Duration > 0)
            {
                using (SolidBrush b = new SolidBrush(Dim)) g.FillRectangle(b, x, y + h - 6, w, 6);
                using (SolidBrush b = new SolidBrush(Yellow)) g.FillRectangle(b, x, y + h - 6, w * Math.Min(1f, (float)m.Offset / m.Duration), 6);
            }
            if (sel) Focus(g, r);
        }

        void PosterRow(Graphics g, List<Movie> l, int row, float y, bool active)
        {
            const float w = 210, h = 315, gap = 32;
            int vis = 7;
            int col = Math.Min(homeCol[row], Math.Max(0, l.Count - 1));
            if (col < homeScroll[row]) homeScroll[row] = col;
            if (col >= homeScroll[row] + vis) homeScroll[row] = col - vis + 1;
            for (int i = homeScroll[row], k = 0; i < l.Count && k < vis; i++, k++)
            {
                float x = 96 + k * (w + gap);
                Movie m = l[i];
                Poster(g, m, i + row * 3, x, y, w, h, active && !navFocus && i == col);
                bool ep = m.Type == "episode";
                TextBox(g, ep ? m.ShowTitle : m.Title, 20, true, Color.White, new RectangleF(x, y + h + 10, w, 28));
                string sub = row == 0 && m.Duration > 0 && m.Offset > 0 ? Left(m.Duration - m.Offset) + " left" : (m.Year > 0 && !ep ? m.Year.ToString(CultureInfo.InvariantCulture) : "");
                if (ep) sub = m.Code + (sub.Length > 0 ? "  \u00b7  " + sub : "");
                if (m.Type == "show" && row == 3) sub = m.SeasonNum > 0 ? "Season " + m.SeasonNum.ToString(CultureInfo.InvariantCulture) + " added" : "New episodes";   // 2.71.0
                Txt(g, sub, 18, false, Grey, x, y + h + 38);
            }
        }

        static string Left(long ms)
        {
            long min = ms / 60000;
            return min >= 60 ? string.Format(CultureInfo.InvariantCulture, "{0}h {1}m", min / 60, min % 60) : min.ToString(CultureInfo.InvariantCulture) + "m";
        }

        void DrawHome(Graphics g)
        {
            // 2.71.0: up to four rows; the page scrolls so the chosen row and the one above show.
            List<int> rows = HomeRows();
            int pos = Math.Max(0, rows.IndexOf(homeRow));
            float y = 130 - Math.Max(0, pos - 1) * 450;
            System.Drawing.Drawing2D.GraphicsState gs = g.Save();
            g.SetClip(new RectangleF(0, 120, 1920, 960));
            string[] names = { "Continue Watching", "Recently Added Movies", "Next Up", "Recently Added TV" };
            foreach (int r in rows)
            {
                if (y > -400 && y < 1080)
                {
                    Txt(g, names[r], 30, true, Color.White, 96, y);
                    List<Movie> l = HomeList(r);
                    if (l.Count > 0) PosterRow(g, l, r, y + 52, homeRow == r);
                    else Txt(g, "Nothing yet.", 22, false, Grey, 96, y + 60);
                }
                y += 450;
            }
            g.Restore(gs);
        }

        void DrawGrid(Graphics g)
        {
            List<Movie> movies = GL();
            int gridIdx = GIdx, gridScroll = GScroll;
            DrawGrid2(g, movies, gridIdx, ref gridScroll);
            GScroll = gridScroll;
        }

        void DrawGrid2(Graphics g, List<Movie> movies, int gridIdx, ref int gridScroll)
        {
            const float w = 210, h = 315, gapX = 32, rowH = 410, top = 130;
            int rows = 2;
            int r = gridIdx / Cols;
            if (r < gridScroll) gridScroll = r;
            if (r >= gridScroll + rows) gridScroll = r - rows + 1;
            if (movies.Count == 0) { Txt(g, scr == Screen2.Shows ? "No TV shows found." : "No movies found.", 24, false, Grey, 96, top); return; }
            for (int i = gridScroll * Cols; i < movies.Count && i < (gridScroll + rows + 1) * Cols; i++)
            {
                int rr = i / Cols - gridScroll, cc = i % Cols;
                float x = 96 + cc * (w + gapX), y = top + rr * rowH;
                if (y > 1000) break;
                Movie m = movies[i];
                Poster(g, m, i, x, y, w, h, !navFocus && i == gridIdx);
                TextBox(g, m.Title, 20, true, Color.White, new RectangleF(x, y + h + 10, w, 28));
                string sub = m.Type == "show" && m.LeafCount > 0
                    ? (m.ViewedLeafCount >= m.LeafCount ? "Watched" : (m.LeafCount - m.ViewedLeafCount).ToString(CultureInfo.InvariantCulture) + " unwatched")
                    : (m.Year > 0 ? m.Year.ToString(CultureInfo.InvariantCulture) : "");
                Txt(g, sub, 18, false, Grey, x, y + h + 38);
            }
            Txt(g, string.Format(CultureInfo.InvariantCulture, "{0} of {1}   (type a letter to jump)", gridIdx + 1, movies.Count), 18, false, Grey, 1420, 1036);
        }

        void DrawDetails(Graphics g)
        {
            Movie m = det;
            Image art = Img(m, m.Art, 1920, 1080);
            if (art != null)
            {
                g.DrawImage(art, new RectangleF(0, 0, 1920, 1080));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, Bg))) g.FillRectangle(b, 0, 0, 1920, 1080);
            }
            Poster(g, m, 0, 96, 140, 360, 540, false);
            float x = 520;
            TextBox(g, m.Title, 56, true, Color.White, new RectangleF(x, 140, 1300, 80));
            List<string> meta = new List<string>();
            if (m.Type == "episode") meta.Add(m.ShowTitle + "  " + m.Code);   // 2.68.0
            if (m.Year > 0 && m.Type != "episode") meta.Add(m.Year.ToString(CultureInfo.InvariantCulture));
            if (m.Duration > 0) meta.Add(Left(m.Duration));
            if (m.Rating.Length > 0) meta.Add(m.Rating);
            Txt(g, string.Join("  ·  ", meta.ToArray()), 24, false, Grey, x, 228);
            float bx = x;
            using (Font f = F(18, true))
                foreach (string b in new[] { m.Res, m.Hdr, m.Audio })
                {
                    if (string.IsNullOrEmpty(b)) continue;
                    SizeF sz = g.MeasureString(b, f);
                    using (Pen p = new Pen(Grey, 2)) g.DrawRectangle(p, bx, 276, sz.Width + 24, 36);
                    using (SolidBrush br = new SolidBrush(Color.White)) g.DrawString(b, f, br, bx + 12, 282);
                    bx += sz.Width + 40;
                }
            TextBox(g, m.Summary, 24, false, Color.FromArgb(213, 216, 222), new RectangleF(x, 340, 1300, 230));
            List<string> btns = DetailButtons();
            float px = x;
            using (Font f = F(26, true))
                for (int i = 0; i < btns.Count; i++)
                {
                    SizeF sz = g.MeasureString(btns[i], f);
                    RectangleF r = new RectangleF(px, 610, sz.Width + 64, 70);
                    bool sel = i == detBtn && !detOnVersion;
                    using (SolidBrush b = new SolidBrush(sel ? Yellow : Card)) g.FillRectangle(b, r);
                    using (SolidBrush b = new SolidBrush(sel ? Bg : Color.White)) g.DrawString(btns[i], f, b, px + 32, 628);
                    if (sel) using (Pen w = new Pen(Color.White, 3)) g.DrawRectangle(w, r.X - 4, r.Y - 4, r.Width + 8, r.Height + 8);
                    px += r.Width + 28;
                }
            if (m.Offset > 0 && m.Duration > 0)
            {
                using (SolidBrush b = new SolidBrush(Dim)) g.FillRectangle(b, x, 712, 600, 6);
                using (SolidBrush b = new SolidBrush(Yellow)) g.FillRectangle(b, x, 712, 600 * Math.Min(1f, (float)m.Offset / m.Duration), 6);
            }
            if (m.Versions.Count > 1)
            {
                // 2.55.0: which version Play uses.
                RectangleF r = new RectangleF(x, 750, 1300, 70);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, r);
                Txt(g, "Version", 24, true, Color.White, x + 28, 769);
                Txt(g, "\u2039  " + m.Versions[m.VerIdx].Label() + "  \u203a", 24, false, Color.White, x + 200, 769);
                Txt(g, (m.VerIdx + 1).ToString(CultureInfo.InvariantCulture) + " of " + m.Versions.Count.ToString(CultureInfo.InvariantCulture), 22, false, Grey, x + 1160, 771);
                if (detOnVersion) Focus(g, r);
            }
            Txt(g, m.Versions.Count > 1 ? "Left / Right: choose    Down: version    Enter: select    Backspace: back"
                                        : "Left / Right: choose    Enter: select    Backspace: back", 18, false, Grey, 96, 1036);
#if PLAYER
            if (nextAt != DateTime.MaxValue && det == nextEp)
            {
                int left = Math.Max(0, (int)Math.Ceiling((nextAt - DateTime.Now).TotalSeconds));
                RectangleF r = new RectangleF(520, 860, 1300, 110);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, r);
                using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, r.X, r.Y, r.Width, 5);
                Txt(g, "Next episode in " + left.ToString(CultureInfo.InvariantCulture) + " s", 30, true, Color.White, r.X + 32, r.Y + 22);
                Txt(g, "Enter: play now     Back: stop", 22, false, Grey, r.X + 32, r.Y + 68);
            }
#endif
        }

        string HelpDelay()
        {
#if PLAYER
            int fr = host.LookAheadFrames();
            return "How far the sound is held back, so it matches the picture. It includes the look-ahead ("
                + (fr <= 0 ? "Off" : (fr >= 12 ? "High Performance" : fr.ToString(CultureInfo.InvariantCulture) + " frames")) + ", "
                + LookAheadMs().ToString(CultureInfo.InvariantCulture) + " ms) and follows it when the look-ahead is changed. Lip sync test sets it for you.";
#else
            return "";
#endif
        }

        void DrawSettings(Graphics g)
        {
            string[] rows = SettingsRows();
            for (int i = 0; i < rows.Length; i++)
            {
                string[] p = rows[i].Split('\t');
                float y = 140 + i * 80;   // 2.69.0: nine rows (was 88 apart)
                RectangleF r = new RectangleF(96, y, 1728, 66);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, r);
                Txt(g, p[0], 26, true, Color.White, 126, y + 16);
                TextBox(g, p.Length > 1 ? p[1] : "", 24, false, Grey, new RectangleF(720, y + 18, 1090, 40));
                if (!navFocus && i == setIdx) Focus(g, r);
            }
            string help = SetRow() == 0 ? HelpDelay() : SetRow() == 2
                ? "Plays a flash with a beep. Left / Right moves the sound 10 ms; Enter keeps it, Esc cancels. Do it once with look-ahead Off (F7 menu)."
                : (SetRow() == 3 ? "All servers shows the movies of every server together; or choose one."
                : (SetRow() == 5 ? "Asks for each button in turn (arrows, OK, Back, Play/Pause, Stop, chapters, skip, Menu) and stores what your remote sends. Wait 8 s to skip a button it doesn't have."
#if PLAYER
                : (SetRow() == 7 ? "Video Processor plays the capture card's PCM sound (a PS5 game set to Linear PCM) on this PC, held back with the picture. Raise it if the sound is early, lower it if late. Dolby / DTS bitstream on the card is muted."
                : "")));
#else
                : ""));
#endif
            Txt(g, help, 20, false, Grey, 96, 880);
        }

        void DrawSignIn(Graphics g)
        {
            Txt(g, AppName, 30, true, Color.White, 96, 40);
            Txt(g, "Sign in to Plex", 48, true, Color.White, 96, 150);
            if (qr != null)
            {
                int n = qr.GetLength(0);
                float mod = (float)Math.Floor(420.0 / (n + 8));
                float size = mod * (n + 8), ox = 1920 - 96 - size, oy = 200;
                using (SolidBrush w = new SolidBrush(Color.White)) g.FillRectangle(w, ox, oy, size, size);
                g.SmoothingMode = SmoothingMode.None;
                using (SolidBrush k = new SolidBrush(Color.Black))
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                            if (qr[y, x]) g.FillRectangle(k, ox + (x + 4) * mod, oy + (y + 4) * mod, mod, mod);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Txt(g, "Scan with your phone", 22, false, Grey, ox, oy + size + 16);
            }
            Txt(g, "On your phone or computer, go to", 28, false, Grey, 96, 270);
            Txt(g, "plex.tv/link", 44, true, Color.White, 96, 316);
            Txt(g, "and enter this code:", 28, false, Grey, 96, 390);
            Txt(g, pinCode, 120, true, Yellow, 90, 430);
            Txt(g, "Waiting for you to sign in...   Esc: back to Video Processor", 22, false, Grey, 96, 640);
        }

        void DrawMessage(Graphics g)
        {
            RectangleF r = new RectangleF(360, 380, 1200, 320);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 30, 32, 38))) g.FillRectangle(b, r);
            using (Pen p = new Pen(Yellow, 4)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            TextBox(g, message, 26, false, Color.White, new RectangleF(r.X + 40, r.Y + 36, r.Width - 80, r.Height - 110));
            Txt(g, "Enter: OK", 22, true, Yellow, r.X + 40, r.Bottom - 60);
        }
    }
}
#endif
