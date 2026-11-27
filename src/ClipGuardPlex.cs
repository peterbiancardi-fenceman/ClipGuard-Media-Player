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
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
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
        // 2.86.0: for the banner's FILE column
        public string Container = "", VCodec = "", VProfile = "", Dovi = "";
        public long Bitrate, VBitrate, BitDepth;
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
        public string Hdr = "", Audio = "", Edition = "";
        public string Dovi = "", DvEl = "";   // 2.89.0: Dolby Vision profile ("7.6"), enhancement layer ("FEL" / "MEL", once played)
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

#if PLAYER
    // 2.86.1: menu sounds through waveOut: a stream of silence (44.1 kHz, 16-bit mono, four
    // 30 ms buffers) with the tones mixed in, so the receiver's audio link stays awake.
    sealed class MenuAudio
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public short tag, channels; public int rate, avgBytes; public short align, bits, extra; }
        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr { public IntPtr data; public uint length, recorded; public IntPtr user; public uint flags, loops; public IntPtr next, reserved; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormat f, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        const int Rate = 44100, BufSamples = 1323, NBuf = 4;
        readonly object gate = new object();
        Thread thread;
        volatile bool run;
        short[] pending;
        int pendingPos;

        public void Start()
        {
            lock (gate)
            {
                if (run) return;
                run = true;
                thread = new Thread(Loop);
                thread.IsBackground = true;
                thread.Start();
            }
        }

        public void Stop()
        {
            Thread t;
            lock (gate) { if (!run) return; run = false; t = thread; thread = null; }
            if (t != null) t.Join(1000);
        }

        // A 16-bit mono WAV made by Tone(): its samples are mixed into the stream.
        public void Play(byte[] wav)
        {
            int n = (wav.Length - 44) / 2;
            if (n <= 0) return;
            short[] smp = new short[n];
            Buffer.BlockCopy(wav, 44, smp, 0, n * 2);
            lock (gate) { pending = smp; pendingPos = 0; }
        }

        void Loop()
        {
            IntPtr h = IntPtr.Zero;
            IntPtr[] hdrs = new IntPtr[NBuf], datas = new IntPtr[NBuf];
            int hs = Marshal.SizeOf(typeof(WaveHdr));
            int flagsOff = (int)Marshal.OffsetOf(typeof(WaveHdr), "flags");
            try
            {
                WaveFormat f = new WaveFormat { tag = 1, channels = 1, rate = Rate, avgBytes = Rate * 2, align = 2, bits = 16, extra = 0 };
                if (waveOutOpen(out h, 0xFFFFFFFF, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { h = IntPtr.Zero; return; }
                short[] buf = new short[BufSamples];
                for (int i = 0; i < NBuf; i++)
                {
                    datas[i] = Marshal.AllocHGlobal(BufSamples * 2);
                    hdrs[i] = Marshal.AllocHGlobal(hs);
                    WaveHdr wh = new WaveHdr { data = datas[i], length = (uint)(BufSamples * 2) };
                    Marshal.StructureToPtr(wh, hdrs[i], false);
                    waveOutPrepareHeader(h, hdrs[i], (uint)hs);
                    Fill(buf, datas[i]);
                    waveOutWrite(h, hdrs[i], (uint)hs);
                }
                while (run)
                {
                    for (int i = 0; i < NBuf; i++)
                    {
                        if ((Marshal.ReadInt32(hdrs[i], flagsOff) & 1) == 0) continue;   // WHDR_DONE
                        Fill(buf, datas[i]);
                        waveOutWrite(h, hdrs[i], (uint)hs);
                    }
                    Thread.Sleep(5);
                }
            }
            catch (Exception) { }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    try { waveOutReset(h); } catch (Exception) { }
                    for (int i = 0; i < NBuf; i++) if (hdrs[i] != IntPtr.Zero) try { waveOutUnprepareHeader(h, hdrs[i], (uint)hs); } catch (Exception) { }
                    try { waveOutClose(h); } catch (Exception) { }
                }
                for (int i = 0; i < NBuf; i++)
                {
                    if (hdrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(hdrs[i]);
                    if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
                }
            }
        }

        void Fill(short[] buf, IntPtr dst)
        {
            Array.Clear(buf, 0, buf.Length);
            lock (gate)
            {
                if (pending != null)
                {
                    int n = Math.Min(buf.Length, pending.Length - pendingPos);
                    Array.Copy(pending, pendingPos, buf, 0, n);
                    pendingPos += n;
                    if (pendingPos >= pending.Length) pending = null;
                }
            }
            Marshal.Copy(buf, 0, dst, buf.Length);
        }
    }
#endif

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
#if STANDALONE
            rq.Headers["X-Plex-Version"] = PlayerProgram.MediaPlayerVersion;
#else
            rq.Headers["X-Plex-Version"] = "1.0.0";
#endif
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
            mv.Edition = Str(m, "editionTitle");   // 2.86.0
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
                v.Container = Str(md, "container");
                v.Bitrate = Num(md, "bitrate");
                v.VCodec = Str(md, "videoCodec");
                v.VProfile = Str(md, "videoProfile");
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
                        if (type == 1 && v.VBitrate == 0)
                        {
                            v.VBitrate = Num(st, "bitrate");
                            v.BitDepth = Num(st, "bitDepth");
                            if (Get(st, "DOVIProfile") != null)
                                v.Dovi = Num(st, "DOVIProfile").ToString(CultureInfo.InvariantCulture) + (Get(st, "DOVIBLCompatID") != null ? "." + Num(st, "DOVIBLCompatID").ToString(CultureInfo.InvariantCulture) : "");
                        }
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
            int keep = mv.Versions.Count == vs.Count ? mv.VerIdx : LastVer(mv, vs);   // 2.92.0: else the version played last
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

        // 2.92.0: the version of a film played last (by its file's part key), so a film with
        // more than one version opens and resumes on that one. Kept in lastver.txt.
        static Dictionary<string, string> lastVer;
        static string LastVerPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "MediaPlayer", "lastver.txt"); } }
        static void LoadLastVer()
        {
            if (lastVer != null) return;
            lastVer = new Dictionary<string, string>();
            try { foreach (string l in File.ReadAllLines(LastVerPath)) { string[] f = l.Split('\t'); if (f.Length == 2) lastVer[f[0]] = f[1]; } } catch (Exception) { }
        }
        static int LastVer(Movie mv, List<Ver> vs)
        {
            if (vs.Count < 2 || mv.Srv == null) return 0;
            string pk;
            lock (typeof(Plex))
            {
                LoadLastVer();
                if (!lastVer.TryGetValue(mv.Srv.Id + ":" + mv.Key, out pk)) return 0;
            }
            for (int i = 0; i < vs.Count; i++) if (vs[i].PartKey == pk) return i;
            return 0;   // that version is gone: the first
        }
        public static void RememberVer(Movie mv, Ver v)
        {
            if (mv == null || v == null || mv.Srv == null || mv.Versions.Count < 2 || v.PartKey.Length == 0) return;
            lock (typeof(Plex))
            {
                LoadLastVer();
                string k = mv.Srv.Id + ":" + mv.Key, old;
                if (lastVer.TryGetValue(k, out old) && old == v.PartKey) return;
                lastVer[k] = v.PartKey;
                try
                {
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string> kv in lastVer) lines.Add(kv.Key + "\t" + kv.Value);
                    File.WriteAllLines(LastVerPath, lines.ToArray());
                }
                catch (Exception) { }
            }
        }

        // The chosen version's badges and file.
        public static void Use(Movie mv)
        {
            if (mv.Versions.Count == 0) return;
            Ver v = mv.Versions[mv.VerIdx];
            mv.Res = v.Res; mv.Hdr = v.Hdr; mv.Audio = v.Audio; mv.PartKey = v.PartKey; mv.File = v.File;
            mv.Dovi = v.Dovi;
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

    // 1.1.0: the graphics card's temperature and fan speed for the Info card, read every
    // 2 s on a timer thread through NVIDIA's management library (nvml.dll, installed with the
    // driver). Read-only: nothing here changes the card. No NVIDIA card or no nvml.dll: no row.
    static class GpuTemp
    {
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] static extern int nvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);

        static System.Threading.Timer timer;
        static IntPtr dev = IntPtr.Zero;
        static volatile int state;                 // 0 not started, 1 running, 2 unavailable
        static volatile int tempC = -1, fanPct = -1;
        static readonly object gate = new object();

        static void Poll(object o)
        {
            lock (gate) PollLocked();
        }

        static void PollLocked()
        {
            if (state == 2) return;
            try
            {
                if (dev == IntPtr.Zero)
                {
                    if (nvmlInit() != 0 || nvmlDeviceGetHandleByIndex(0, out dev) != 0 || dev == IntPtr.Zero) { dev = IntPtr.Zero; Stop(); return; }
                }
                uint t, f;
                tempC = nvmlDeviceGetTemperature(dev, 0, out t) == 0 ? (int)t : -1;   // 0 = NVML_TEMPERATURE_GPU
                fanPct = nvmlDeviceGetFanSpeed(dev, out f) == 0 ? (int)f : -1;
            }
            catch (Exception) { Stop(); }
        }

        static void Stop()
        {
            state = 2; tempC = -1; fanPct = -1;
            if (timer != null) { timer.Dispose(); timer = null; }   // called with gate held
        }

        // 1.1.0: a fresh reading now (for the F11 log), whatever the Info card has done.
        public static string ReadNow()
        {
            lock (gate) PollLocked();
            return Text();
        }

        // "62 °C, fan 35%", or "" when there is nothing to show.
        public static string Text()
        {
            if (state == 0)
                lock (gate)
                    if (state == 0) { state = 1; timer = new System.Threading.Timer(Poll, null, 0, 2000); }
            int t = tempC, f = fanPct;
            if (t < 0) return "";
            return t.ToString(CultureInfo.InvariantCulture) + " \u00B0C" + (f >= 0 ? ", fan " + f.ToString(CultureInfo.InvariantCulture) + "%" : "");
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
            int NeuralUpscaler();             // 2.85.0: 0 none, 1 FSRCNNX, 2 ArtCNN C4F32
            void SetNeuralUpscaler(int v);
            bool Ssr();                       // 2.85.0: SSimSuperRes
            void SetSsr(bool on);
            int OutGamma();                   // 2.88.1: 0 = 2.2, 1 = 2.4, 2 = BT.1886
            void SetOutGamma(int v);
            int DepthLevel();                 // 2.94.0: Depth 0-100 (0 off)
            void SetDepthLevel(int v);
            void ToggleClipGuardBanner();     // 2.83.0: F6, from the Home button
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

        // 2.75.0: the colours come from the theme (Settings > Theme). "Yellow" is the theme's
        // highlight; focus always has a white outline as well.
        static Color Bg = Color.FromArgb(14, 15, 18), Grey = Color.FromArgb(167, 171, 179), Card = Color.FromArgb(34, 37, 43),
            Yellow = Color.FromArgb(255, 212, 0), Dim = Color.FromArgb(42, 45, 51);
        static readonly string[] ThemeNames = { "Midnight", "Cinema", "Ocean", "Graphite", "High Contrast" };
        static readonly Color[][] Themes = {
            // background, card, dim, grey text, highlight
            new[] { Color.FromArgb(14, 15, 18), Color.FromArgb(34, 37, 43), Color.FromArgb(42, 45, 51), Color.FromArgb(167, 171, 179), Color.FromArgb(255, 212, 0) },
            new[] { Color.FromArgb(10, 6, 6), Color.FromArgb(72, 18, 22), Color.FromArgb(52, 22, 24), Color.FromArgb(196, 172, 172), Color.FromArgb(255, 212, 0) },
            new[] { Color.FromArgb(6, 12, 24), Color.FromArgb(22, 44, 82), Color.FromArgb(28, 40, 64), Color.FromArgb(168, 184, 208), Color.FromArgb(240, 244, 255) },
            new[] { Color.FromArgb(12, 12, 12), Color.FromArgb(40, 40, 40), Color.FromArgb(50, 50, 50), Color.FromArgb(160, 160, 160), Color.FromArgb(236, 236, 236) },
            new[] { Color.FromArgb(0, 0, 0), Color.FromArgb(22, 22, 22), Color.FromArgb(70, 70, 70), Color.FromArgb(220, 220, 220), Color.FromArgb(255, 220, 0) } };
        static int themeIdx;
        static void ApplyTheme(int i)
        {
            themeIdx = Math.Max(0, Math.Min(Themes.Length - 1, i));
            Color[] t = Themes[themeIdx];
            Bg = t[0]; Card = t[1]; Dim = t[2]; Grey = t[3]; Yellow = t[4];
        }
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

        enum Screen2 { Busy, SignIn, Home, Movies, Details, Settings, VP, Picture, ClipGuard, Shows, Show, Starting, Power }
#if STANDALONE
        const string AppName = "ClipGuard Media Player";
#else
        const string AppName = "ClipGuard+ Media Player";
#endif
        // 2.64.0: the Player has Video Processor (capture) and Picture (its own settings) tabs.
#if PLAYER
        // 2.73.0: Power, the only way to close the player (or shut the PC down).
        const int TabTV = 2, TabVP = 3, TabPicture = 4, TabClipGuard = 5, TabSettings = 6, TabPower = 7, TabCount = 8;
        static readonly string[] TabNames = { "Home", "Movies", "TV Shows", "Video Processor", "Picture", "ClipGuard", "Settings", "Power" };
#else
        const int TabTV = -1, TabVP = -1, TabPicture = -1, TabClipGuard = -1, TabSettings = 2, TabPower = -1, TabCount = 3;
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
            if (t == TabPower) return Screen2.Power;
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
        DateTime playStarted = DateTime.MinValue;   // 2.89.0
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

            int th;
            ApplyTheme(int.TryParse(Ini("Theme", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out th) ? th : 0);   // 2.75.0
            ApplyLowLatency();   // 1.1.0
            BackColor = Bg;
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
            playTimer.Tick += delegate { PlayTick(); FileInfoTick(); };
#if PLAYER
            playTimer.Tick += delegate { if (paused && playing != null && DateTime.Now < pausedRedrawUntil) UnpluggedSettings.Redraw(); };   // 0.9.4
#endif
#if PLAYER
            if (PHost != null) { IPlayerHost ph = PHost; UnpluggedSettings.GetFsr = ph.NeuralUpscaler; UnpluggedSettings.SetFsr = ph.SetNeuralUpscaler; }
#if PLAYER
            UnpluggedSettings.GetCg = ChromaCg;   // 2.111.0
            UnpluggedSettings.SetCg = delegate(int v) { SetIni("ChromaGuided", Math.Max(0, Math.Min(2, v)).ToString(CultureInfo.InvariantCulture)); PublishView(); };
            UnpluggedSettings.GetSsim = SsimDown;   // 0.9.6
            UnpluggedSettings.SetSsim = delegate(int v) { SetIni("SsimDown", v == 1 ? "1" : "0"); PublishView(); };
#endif
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
            focusTimer.Tick += delegate { KeepFocus(); MenuAudioTick(); };   // 2.86.1: menu sound stream
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
            StartMouseTimer();   // 0.9.2: mouse
            // Video Processor stops while the library is up (and a film needs Unplugged closed first).
            Bg2(delegate { CloseUnplugged(); }, delegate { Start(); });
        }

#if PLAYER
        // ---- anamorphic lens and 12-point screen geometry ----------
        // Saved as "Screen.screen" in the Calibration folder (next to the LUTs) and loaded
        // at every start. The renderer reads it through
        // Local\ClipGuard_view_v1: lens x100, screen x100, the film's aspect x1000 (0 = the
        // frame's own, -1 = not scope), geometry on, 0, then the 12 points' moves (4 across x
        // 3 down, row by row, x / y in 1/10000 of the width / height).
        sealed class ScreenProf
        {
            public int Lens, Screen = 240, Mode;   // Mode: 0 Auto, 1 Scope, 2 Not scope
            public bool GeoOn = true;
            public int[] Pt = new int[24];
        }
        static readonly int[] LensVals = { 0, 125, 133, 150 };
        ScreenProf scrProf = new ScreenProf();
        int viewAspect;   // the film's aspect sent with the lens (x1000; 0 frame, -1 not scope)
        MemoryMappedFile viewMap;
        MemoryMappedViewAccessor viewAcc;
        int viewSeq;
        int viewFilm;   // 0.9.5: counts films, so the renderer starts each one's scope check afresh

        string ScreenFile() { return Path.Combine(CalDir, "Screen.screen"); }

        void LoadScreenProfile()
        {
            ScreenProf p = new ScreenProf();
            bool layout2 = false;
            try
            {
                if (File.Exists(ScreenFile()))
                    foreach (string line in File.ReadAllLines(ScreenFile()))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                        int n;
                        if (k == "lens" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) p.Lens = Array.IndexOf(LensVals, n) >= 0 ? n : 0;
                        else if (k == "screen" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) p.Screen = n == 235 ? 235 : 240;
                        else if (k == "shape" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) p.Mode = Math.Max(0, Math.Min(2, n));
                        else if (k == "geometry") p.GeoOn = v != "0";
                        else if (k == "layout" && v == "2") layout2 = true;   // 0.9.5
                        else if (k == "points")
                        {
                            string[] parts = v.Split(',');
                            for (int i = 0; i < 24 && i < parts.Length; i++)
                                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) p.Pt[i] = Math.Max(-2500, Math.Min(2500, n));
                        }
                    }
            }
            catch (Exception) { }
            if (!layout2) p.Pt = new int[24];   // 0.9.5: points from the old layout (4 x 3) start again
            scrProf = p;
            PublishView();
        }

        void SaveScreenProfile()
        {
            try
            {
                Directory.CreateDirectory(CalDir);
                List<string> pts = new List<string>();
                foreach (int v in scrProf.Pt) pts.Add(v.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(ScreenFile(), new[] {
                    "; ClipGuard screen: anamorphic lens and 12-point geometry",
                    "lens=" + scrProf.Lens.ToString(CultureInfo.InvariantCulture),
                    "screen=" + scrProf.Screen.ToString(CultureInfo.InvariantCulture),
                    "shape=" + scrProf.Mode.ToString(CultureInfo.InvariantCulture),
                    "geometry=" + (scrProf.GeoOn ? "1" : "0"),
                    "layout=2",
                    "points=" + string.Join(",", pts.ToArray()) });
            }
            catch (Exception) { }
            PublishView();
        }

        // Once per film (or capture): the film's shape for the lens. It isn't changed while
        // the film plays.
        void ViewForFilm(Movie m)
        {
            // 0.9.5: Plex's aspect is the file's (a letterboxed scope film reads 1.78), so Auto
            // leaves it to the renderer, which reads the letterbox once at the film's start.
            viewAspect = scrProf.Mode == 1 ? 2400 : (scrProf.Mode == 2 ? -1 : 0);
            viewFilm++;
            PublishView();
        }

        void PublishView()
        {
            try
            {
                if (viewAcc == null)
                {
                    viewMap = MemoryMappedFile.CreateOrOpen(@"Local\ClipGuard_view_v1", 4096);
                    viewAcc = viewMap.CreateViewAccessor(0, 4096);
                }
                int mode = scrProf.Mode;
                int aspect = mode == 1 ? 2400 : (mode == 2 ? -1 : viewAspect);
                viewAcc.Write(8, scrProf.Lens);
                viewAcc.Write(12, scrProf.Screen);
                viewAcc.Write(16, aspect);
                viewAcc.Write(20, scrProf.GeoOn ? 1 : 0);
                viewAcc.Write(24, viewFilm);
                for (int i = 0; i < 24; i++) viewAcc.Write(28 + i * 4, scrProf.Pt[i]);
                int cbs = ClipBitState();   // 2.109.0: ClipBit decides
                viewAcc.Write(124, cbs != 0 ? DetDetail() : 0);
                viewAcc.Write(128, cbs != 0 ? DetGrain() : 0);
                viewAcc.Write(132, cbs);
                viewAcc.Write(136, (int)Math.Min(int.MaxValue, ClipBitKbps()));
                viewAcc.Write(140, ClipBitMode());
                viewAcc.Write(144, LensUp());   // 2.110.0 (2.115.0: 0 Standard, 1 Neural 2x first, 2 Lightweight)
                viewAcc.Write(148, ChromaCg());   // 2.111.0
                viewAcc.Write(152, Ini("Ravu", "1") == "1" ? 1 : 0);   // 2.114.0: RAVU-Zoom-AR for upscales
                viewAcc.Write(156, SsimDown());   // 2.114.0: SSimDownscaler for downscales
                // 1.0.0: the theme, so the renderer's banner and in-film panel match the player
                viewAcc.Write(168, Bg.ToArgb() & 0xFFFFFF);
                viewAcc.Write(172, Card.ToArgb() & 0xFFFFFF);
                viewAcc.Write(176, Dim.ToArgb() & 0xFFFFFF);
                viewAcc.Write(180, Grey.ToArgb() & 0xFFFFFF);
                viewAcc.Write(184, Yellow.ToArgb() & 0xFFFFFF);
                viewAcc.Write(164, 1);
                viewAcc.Write(4, ++viewSeq);
                viewAcc.Write(0, 0x56574543);
            }
            catch (Exception) { }
            if (playing != null && paused) UnpluggedSettings.Redraw();
        }

        // ---- Define screen: the editor (the player's own screen, drawn through the same
        // warp the renderer uses, so what lines up here lines up on films) ----------------
        bool geoEdit;
        int geoSel;
        int[] geoUndo;
        Keys geoLastKey;
        DateTime geoLastAt;
        int geoRun;
        // 0.9.5: all 12 on the edges: 0-3 the top (left to right), 4-7 the bottom, 8-9 the
        // left side (upper, lower third), 10-11 the right side. OK goes round clockwise.
        static readonly string[] GeoPointNames = {
            "top left corner", "top, left third", "top, right third", "top right corner",
            "bottom left corner", "bottom, left third", "bottom, right third", "bottom right corner",
            "left side, upper third", "left side, lower third", "right side, upper third", "right side, lower third" };
        static readonly int[] GeoOrder = { 0, 1, 2, 3, 10, 11, 7, 6, 5, 4, 9, 8 };
        static readonly float[] GeoU = { 0f, 1f / 3f, 2f / 3f, 1f, 0f, 1f / 3f, 2f / 3f, 1f, 0f, 0f, 1f, 1f };
        static readonly float[] GeoV = { 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f, 1f / 3f, 2f / 3f, 1f / 3f, 2f / 3f };

        void GeoEditStart()
        {
            geoUndo = (int[])scrProf.Pt.Clone();
            geoEdit = true;
            geoSel = 0;
            Invalidate();
        }

        bool GeoEditKey(Keys k)
        {
            if (k == Keys.Enter) { geoSel = GeoOrder[(Array.IndexOf(GeoOrder, geoSel) + 1) % 12]; Invalidate(); return true; }
            if (k == Keys.Back || k == Keys.BrowserBack || k == Keys.Escape)
            {
                geoEdit = false;
                SaveScreenProfile();
                message = "Screen geometry saved.";
                Invalidate();
                return true;
            }
            if (k == Keys.MediaStop) { scrProf.Pt = (int[])geoUndo.Clone(); Invalidate(); return true; }   // back to how it was
            if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down)
            {
                // 1 pixel (at 4K) a press; held or pressed quickly, faster (up to 16)
                DateTime now = DateTime.Now;
                geoRun = (k == geoLastKey && (now - geoLastAt).TotalMilliseconds < 300) ? Math.Min(geoRun + 1, 40) : 0;
                geoLastKey = k; geoLastAt = now;
                int px = geoRun < 6 ? 1 : (geoRun < 16 ? 4 : 16);
                int i = geoSel * 2;
                if (k == Keys.Left) scrProf.Pt[i] -= (int)Math.Round(px * 10000.0 / 3840);
                if (k == Keys.Right) scrProf.Pt[i] += (int)Math.Round(px * 10000.0 / 3840);
                if (k == Keys.Up) scrProf.Pt[i + 1] -= (int)Math.Round(px * 10000.0 / 2160);
                if (k == Keys.Down) scrProf.Pt[i + 1] += (int)Math.Round(px * 10000.0 / 2160);
                scrProf.Pt[i] = Math.Max(-2500, Math.Min(2500, scrProf.Pt[i]));
                scrProf.Pt[i + 1] = Math.Max(-2500, Math.Min(2500, scrProf.Pt[i + 1]));
                Invalidate();
                return true;
            }
            return true;
        }

        // The same surface as the renderer: each edge a smooth curve through its 4
        // points (a corner, two thirds, a corner), the inside blended from the four edges
        // (a Coons patch), so nothing inside can kink.
        static float Lag4(float t, float a, float b, float c, float d)
        {
            float[] n = { 0f, 1f / 3f, 2f / 3f, 1f }, v = { a, b, c, d };
            float r = 0;
            for (int j = 0; j < 4; j++) { float l = 1; for (int m = 0; m < 4; m++) if (m != j) l *= (t - n[m]) / (n[j] - n[m]); r += l * v[j]; }
            return r;
        }
        // ---- 2.108.0: the screen geometry on the player's own screens (menus, library, and the
        // playback bar and banners over the film). The renderer only bends the film; these are
        // drawn by the player, so each is drawn whole, then bent the same way: a map from each
        // screen pixel to the spot it shows (made once from the 12 points, the renderer's
        // 48 x 27 mesh filled in), then bilinear. Keyed: the overlay's see-through colour
        // stays exact, so its shape stays see-through.
        bool GeoActive()
        {
            if (!scrProf.GeoOn || geoEdit || scrProf.Pt == null) return false;
            foreach (int v in scrProf.Pt) if (v != 0) return true;
            return false;
        }
        sealed class GeoMap
        {
            public int W, H; public string Key;
            public int[] Src;      // source pixel index (top-left tap), -1 = outside the picture
            public byte[] Fx, Fy;  // the bilinear fractions, 0-255
        }
        static GeoMap geoMap;
        static int[] geoBufA, geoBufO;
        static readonly object geoMapLock = new object();
        static GeoMap GetGeoMap(int[] pt, int W, int H)
        {
            string key = W + "x" + H + ":" + string.Join(",", Array.ConvertAll(pt, delegate(int x) { return x.ToString(CultureInfo.InvariantCulture); }));
            lock (geoMapLock)
            {
                if (geoMap != null && geoMap.Key == key) return geoMap;
                GeoMap m = new GeoMap { W = W, H = H, Key = key, Src = new int[W * H], Fx = new byte[W * H], Fy = new byte[W * H] };
                for (int i = 0; i < m.Src.Length; i++) m.Src[i] = -1;
                const int NX = 48, NY = 27;
                PointF[,] P = new PointF[NX + 1, NY + 1];
                for (int j = 0; j <= NY; j++) for (int i = 0; i <= NX; i++) P[i, j] = GeoWarp(pt, i / (float)NX, j / (float)NY, W, H);
                Action<PointF, PointF, PointF, PointF, PointF, PointF> tri = delegate(PointF a, PointF b, PointF c, PointF ua, PointF ub, PointF uc)
                {
                    float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (Math.Abs(den) < 1e-6f) return;
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
                    int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                    for (int y = y0; y <= y1; y++)
                    {
                        float py = y + 0.5f;
                        for (int x = x0; x <= x1; x++)
                        {
                            float px = x + 0.5f;
                            float l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / den;
                            float l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / den;
                            float l3 = 1 - l1 - l2;
                            if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                            float sxp = (l1 * ua.X + l2 * ub.X + l3 * uc.X) * W - 0.5f;
                            float syp = (l1 * ua.Y + l2 * ub.Y + l3 * uc.Y) * H - 0.5f;
                            sxp = Math.Max(0, Math.Min(W - 1.001f, sxp)); syp = Math.Max(0, Math.Min(H - 1.001f, syp));
                            int ix = (int)sxp, iy = (int)syp, o = y * W + x;
                            m.Src[o] = iy * W + ix;
                            m.Fx[o] = (byte)((sxp - ix) * 255); m.Fy[o] = (byte)((syp - iy) * 255);
                        }
                    }
                };
                for (int j = 0; j < NY; j++)
                    for (int i = 0; i < NX; i++)
                    {
                        PointF u00 = new PointF(i / (float)NX, j / (float)NY), u10 = new PointF((i + 1) / (float)NX, j / (float)NY);
                        PointF u01 = new PointF(i / (float)NX, (j + 1) / (float)NY), u11 = new PointF((i + 1) / (float)NX, (j + 1) / (float)NY);
                        tri(P[i, j], P[i + 1, j], P[i, j + 1], u00, u10, u01);
                        tri(P[i + 1, j], P[i + 1, j + 1], P[i, j + 1], u10, u11, u01);
                    }
                geoMap = m;
                return m;
            }
        }
        // Bends src (32bpp, W x H) into a new bitmap. key != 0: that colour stays exact (taps of
        // it are left out of the mix; a pixel nearest to it stays it), outside = key; else black.
        static Bitmap GeoBend(Bitmap src, int[] pt, int key)
        {
            int W = src.Width, H = src.Height;
            GeoMap m = GetGeoMap(pt, W, H);
            System.Drawing.Imaging.PixelFormat pf = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            Rectangle r = new Rectangle(0, 0, W, H);
            if (geoBufA == null || geoBufA.Length != W * H) { geoBufA = new int[W * H]; geoBufO = new int[W * H]; }   // kept: no 4K garbage per redraw
            int[] a = geoBufA, o = geoBufO;
            System.Drawing.Imaging.BitmapData bd = src.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, pf);
            try { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, a, 0, a.Length); } finally { src.UnlockBits(bd); }
            int outside = key != 0 ? key : unchecked((int)0xFF000000);
            System.Threading.Tasks.Parallel.For(0, H, delegate(int y)
            {
                int row = y * W;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x, s0 = m.Src[i];
                    if (s0 < 0) { o[i] = outside; continue; }
                    int fx = m.Fx[i], fy = m.Fy[i];
                    int sx = s0 % W;
                    int s1 = sx < W - 1 ? s0 + 1 : s0, s2 = s0 + W < a.Length ? s0 + W : s0, s3 = s2 + (s1 - s0);
                    int c0 = a[s0], c1 = a[s1], c2 = a[s2], c3 = a[s3];
                    int w0 = (255 - fx) * (255 - fy), w1 = fx * (255 - fy), w2 = (255 - fx) * fy, w3 = fx * fy;
                    if (key != 0)
                    {
                        int near = fx < 128 ? (fy < 128 ? c0 : c2) : (fy < 128 ? c1 : c3);
                        if (near == key) { o[i] = key; continue; }
                        if (c0 == key) w0 = 0; if (c1 == key) w1 = 0; if (c2 == key) w2 = 0; if (c3 == key) w3 = 0;
                    }
                    int wt = w0 + w1 + w2 + w3;
                    if (wt <= 0) { o[i] = c0; continue; }
                    int rr = (((c0 >> 16) & 255) * w0 + ((c1 >> 16) & 255) * w1 + ((c2 >> 16) & 255) * w2 + ((c3 >> 16) & 255) * w3) / wt;
                    int gg = (((c0 >> 8) & 255) * w0 + ((c1 >> 8) & 255) * w1 + ((c2 >> 8) & 255) * w2 + ((c3 >> 8) & 255) * w3) / wt;
                    int bb = ((c0 & 255) * w0 + (c1 & 255) * w1 + (c2 & 255) * w2 + (c3 & 255) * w3) / wt;
                    int px = unchecked((int)0xFF000000) | (rr << 16) | (gg << 8) | bb;
                    if (key != 0 && px == key) px ^= 0x010000;   // a mix that lands on the key would show through
                    o[i] = px;
                }
            });
            Bitmap dst = new Bitmap(W, H, pf);
            bd = dst.LockBits(r, System.Drawing.Imaging.ImageLockMode.WriteOnly, pf);
            try { System.Runtime.InteropServices.Marshal.Copy(o, 0, bd.Scan0, o.Length); } finally { dst.UnlockBits(bd); }
            return dst;
        }
        int[] GeoPts() { return GeoActive() ? (int[])scrProf.Pt.Clone() : null; }

        static PointF GeoWarp(int[] pt, float u, float v, float W, float H)
        {
            float[] d = new float[2];
            for (int c = 0; c < 2; c++)
            {
                Func<int, float> P = delegate(int i) { return pt[i * 2 + c] / 10000f; };
                float T = Lag4(u, P(0), P(1), P(2), P(3)), B = Lag4(u, P(4), P(5), P(6), P(7));
                float L = Lag4(v, P(0), P(8), P(9), P(4)), R = Lag4(v, P(3), P(10), P(11), P(7));
                float corners = (1 - u) * (1 - v) * P(0) + u * (1 - v) * P(3) + (1 - u) * v * P(4) + u * v * P(7);
                d[c] = (1 - v) * T + v * B + (1 - u) * L + u * R - corners;
            }
            return new PointF((u + d[0]) * W, (v + d[1]) * H);
        }

        void DrawGeoEdit(Graphics g)
        {
            GraphicsState st = g.Save();
            g.ResetTransform();
            float W = ClientSize.Width, H = ClientSize.Height;
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int[] pt = scrProf.Pt;
            float lw = Math.Max(1f, H / 1080f);
            // the grid: 12 x 6, the lines through the points stronger
            using (Pen thin = new Pen(Color.FromArgb(150, 150, 150), lw))
            using (Pen thick = new Pen(Color.White, lw * 2.5f))
            {
                for (int i = 0; i <= 12; i++)
                {
                    float u = i / 12f;
                    PointF[] line = new PointF[49];
                    for (int s2 = 0; s2 <= 48; s2++) line[s2] = GeoWarp(pt, u, s2 / 48f, W, H);
                    g.DrawLines(i % 4 == 0 ? thick : thin, line);
                }
                for (int j = 0; j <= 6; j++)
                {
                    float v = j / 6f;
                    PointF[] line = new PointF[65];
                    for (int s2 = 0; s2 <= 64; s2++) line[s2] = GeoWarp(pt, s2 / 64f, v, W, H);
                    g.DrawLines(j % 2 == 0 ? thick : thin, line);   // 0.9.5: the edges and the side points' thirds
                }
            }
            // the 12 points
            float R = 22 * lw;
            for (int i = 0; i < 12; i++)
            {
                PointF c = GeoWarp(pt, GeoU[i], GeoV[i], W, H);
                RectangleF rr = new RectangleF(c.X - R, c.Y - R, 2 * R, 2 * R);
                if (i == geoSel)
                {
                    using (SolidBrush b = new SolidBrush(Yellow)) g.FillEllipse(b, rr);
                    using (Pen p = new Pen(Color.White, lw * 4)) g.DrawEllipse(p, rr);
                }
                else using (Pen p = new Pen(Color.White, lw * 3)) g.DrawEllipse(p, rr);
            }
            // the instructions, in the middle
            g.Restore(st);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 14, 15, 18))) g.FillRectangle(b, 560, 400, 800, 230);
            using (Pen p = new Pen(Color.White, 2)) g.DrawRectangle(p, 560, 400, 800, 230);
            Txt(g, "DEFINE SCREEN", 26, true, Color.FromArgb(232, 32, 42), 600, 420);
            Txt(g, "Point " + (Array.IndexOf(GeoOrder, geoSel) + 1).ToString(CultureInfo.InvariantCulture) + " of 12:  " + GeoPointNames[geoSel], 30, true, Color.White, 600, 470);
            Txt(g, "Arrows move it onto the screen's edge (hold for faster)", 22, false, Color.White, 600, 525);
            Txt(g, "OK: next point     Back: save     Stop: undo all", 22, true, Color.White, 600, 565);
        }
#endif


        bool startedOnce;
        void Start()
        {
#if PLAYER
            // 2.64.0: Picture > Start in: Video Processor starts the capture at once; the
            // library loads behind it.
            if (!startedOnce) { startedOnce = true; LoadScreenProfile(); if (Ini("StartIn", "Library") == "VideoProcessor") BeginInvoke((MethodInvoker)delegate { StartCapture(); }); }
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
            d["re"] = m.Res; d["h"] = m.Hdr; d["au"] = m.Audio; d["dv"] = m.Dovi;
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
            m.Res = Plex.Str(o, "re"); m.Hdr = Plex.Str(o, "h"); m.Audio = Plex.Str(o, "au"); m.Dovi = Plex.Str(o, "dv");
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
                all.Sort(delegate(Movie a, Movie b) { return string.Compare(SortTitle(a.Title ?? ""), SortTitle(b.Title ?? ""), StringComparison.OrdinalIgnoreCase); });
                tv.Sort(delegate(Movie a, Movie b) { return string.Compare(SortTitle(a.Title ?? ""), SortTitle(b.Title ?? ""), StringComparison.OrdinalIgnoreCase); });
                movies = all; recent = rec; shows = tv; recentTv = rtv;
                SplitContinue(c);
                HdrFill();   // 2.89.0: each film's HDR type for the cover tags
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
            Keys tk = Remote.Translate(remote, keyData);
            if (OnKey(tk)) { MenuSound(tk & Keys.KeyCode); Invalidate(); return true; }   // 2.86.0: menu sounds
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
                    if (DateTime.Now >= learnDeadline) { learnMap[learnIdx] = KeptButton(learnIdx); LearnNext(); }   // 2.80.0: a skipped button keeps what it had
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

        string KeptButton(int i)
        {
            string v = remote != null && i < remote.Length ? (remote[i] ?? "") : "";
            return v.Length > 0 && Remote.Find(learnMap, v) < 0 ? v : "";
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
            if (aboutOn) return AboutKey(k);   // 2.89.1
            if (geoEdit) return GeoEditKey(k);   // 0.9.5
            // 2.73.0: Esc is Back; the player closes only from the Power tab (Alt+F4 stays as a
            // keyboard-only way out).
            if (k == (Keys.Alt | Keys.F4)) { Quit(); return true; }
            if (k == Keys.Escape) k = Keys.Back;
            // 2.74.0: the remote's two shortcut buttons (Ctrl+V, Ctrl+M on a keyboard).
            if (k == (Keys.Control | Keys.V)) { if (scr != Screen2.Busy && scr != Screen2.SignIn) StartCapture(); return true; }
            if (k == (Keys.Control | Keys.M))
            {
                if (scr == Screen2.Starting) CancelStarting();
                if (scr != Screen2.Busy && scr != Screen2.SignIn) { scr = Screen2.Home; tab = 0; navFocus = false; }
                return true;
            }
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
#if !PLAYER
                if (back) { ExitToProcessor(); return true; }
#endif
                return true;
            }
            if (scr == Screen2.Details) return DetailsKey(k, back);
#if PLAYER
            if (scr == Screen2.Show) return ShowKey(k, back);
#endif
            if (sortOpen) return SortKey(k, back);   // 2.86.1
            if (navFocus)
            {
                // 2.86.1: OK on Movies or TV Shows opens the sort menu (Down goes into the grid).
                if (k == Keys.Enter && (scr == Screen2.Movies || scr == Screen2.Shows)) { sortOpen = true; sortIdx = SortMode(); return true; }
                if (k == Keys.Left) tab = (tab + TabCount - 1) % TabCount;
                else if (k == Keys.Right) tab = (tab + 1) % TabCount;
                else if (k == Keys.Down || k == Keys.Enter)
                {
#if PLAYER
                    if (tab == TabVP && k == Keys.Enter) { StartCapture(); return true; }
#endif
                    navFocus = false;
                }
#if PLAYER
                else if (back) { tab = 0; }   // 2.73.0: Back on the menu goes Home, never closes
#else
                else if (back) { ExitToProcessor(); return true; }
#endif
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
                case Screen2.Power: return PowerKey(k);
#endif
            }
            return false;
        }

#if PLAYER
        // ---- 2.73.0: Power: close the player, or shut the PC down ------------------------
        int pwrIdx;
        bool pwrConfirm;

        bool PowerKey(Keys k)
        {
            if (pwrConfirm)
            {
                if (k == Keys.Enter) { pwrConfirm = false; ShutDownPc(); }
                else if (k == Keys.Back || k == Keys.BrowserBack) pwrConfirm = false;
                return true;
            }
            if (k == Keys.Up) { if (pwrIdx == 0) navFocus = true; else pwrIdx--; }
            else if (k == Keys.Down) pwrIdx = Math.Min(2, pwrIdx + 1);
            else if (k == Keys.Enter) { if (pwrIdx == 0) RestartPlayer(); else if (pwrIdx == 1) Quit(); else pwrConfirm = true; }
            else return false;
            return true;
        }

        // 2.78.0: Restart: a small hidden PowerShell waits for this process to end, then starts
        // the player again (which also picks up an update).

        void RestartPlayer()
        {
            try
            {
                string exe = Application.ExecutablePath.Replace("'", "''");
                int pid = Process.GetCurrentProcess().Id;
                ProcessStartInfo si = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id " + pid.ToString(CultureInfo.InvariantCulture)
                    + " -Timeout 20 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; Start-Process -FilePath '" + exe + "'\"");
                si.CreateNoWindow = true;
                si.UseShellExecute = false;
                si.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(si);
            }
            catch (Exception ex) { message = "The player could not be restarted: " + ex.Message; Invalidate(); return; }
            Quit();
        }

        void ShutDownPc()
        {
            // Windows waits 5 s, so the player closes cleanly first (Plex told where you
            // stopped, the projector's refresh rate restored).
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("shutdown.exe", "/s /t 5");
                si.CreateNoWindow = true;
                si.UseShellExecute = false;
                Process.Start(si);
            }
            catch (Exception ex) { message = "Windows did not start the shutdown: " + ex.Message; Invalidate(); return; }
            Quit();
        }

        void DrawPower(Graphics g)
        {
            string[][] rows = {
                new[] { "Restart ClipGuard Media Player", "Press Enter   (opens again straight away)" },
                new[] { "Close ClipGuard Media Player", "Press Enter" },
                new[] { "Shut down the HTPC", "Press Enter   (asks first)" } };
            for (int i = 0; i < rows.Length; i++)
            {
                float y = 150 + i * 88;
                RectangleF r = new RectangleF(96, y, 1728, 70);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, r);
                Txt(g, rows[i][0], 26, true, Color.White, 126, y + 18);
                Txt(g, rows[i][1], 24, false, Grey, 720, y + 20);
                { int ii = i; Hit(r, delegate { navFocus = false; pwrIdx = ii; }); }   // 0.9.2: mouse
                if (!navFocus && i == pwrIdx) Focus(g, r);
            }
            if (pwrConfirm)
            {
                RectangleF c = new RectangleF(560, 400, 800, 230);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, c);
                using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, c.X, c.Y, c.Width, 5);
                using (Pen w = new Pen(Color.White, 3)) g.DrawRectangle(w, c.X, c.Y, c.Width, c.Height);
                Txt(g, "Shut down the HTPC?", 34, true, Color.White, c.X + 40, c.Y + 40);
                Txt(g, "Enter: yes        Back: no", 26, false, Grey, c.X + 40, c.Y + 130);
            }
        }
#endif

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
        List<Movie> GL() { return SortedView(scr == Screen2.Shows); }

        // ---- 2.86.1: sorting and filtering Movies and TV Shows ----------------------------
        static readonly string[] SortNames = { "Title (A-Z)", "Date added (newest first)", "Release year (newest first)", "Release year (oldest first)", "Running time (shortest first)" };
        static readonly string[] SortShort = { "Title", "Date added", "Newest", "Oldest", "Running time" };
        bool sortOpen;
        int sortIdx;
        string SortKeyName(bool tv) { return tv ? "SortShows" : "SortMovies"; }
        int SortMode() { return SortModeOf(scr == Screen2.Shows); }
        int SortModeOf(bool tv) { int v; return int.TryParse(Ini(SortKeyName(tv), "0"), out v) ? Math.Max(0, Math.Min(SortNames.Length - 1, v)) : 0; }
        bool UnwatchedOf(bool tv) { return Ini(tv ? "UnwatchedShows" : "UnwatchedMovies", "0") == "1"; }

        List<Movie> svSrcM, svSrcS, svViewM, svViewS;
        int svCountM = -1, svCountS = -1;
        string svKeyM = "", svKeyS = "";

        List<Movie> SortedView(bool tv)
        {
            List<Movie> src = tv ? shows : movies;
            int mode = SortModeOf(tv);
            bool uw = UnwatchedOf(tv);
            string key = mode.ToString(CultureInfo.InvariantCulture) + (uw ? "u" : "a");
            if (tv && src == svSrcS && src.Count == svCountS && key == svKeyS && svViewS != null) return svViewS;
            if (!tv && src == svSrcM && src.Count == svCountM && key == svKeyM && svViewM != null) return svViewM;
            List<Movie> v = uw ? src.FindAll(delegate(Movie m) { return tv ? (m.LeafCount <= 0 || m.ViewedLeafCount < m.LeafCount) : m.ViewCount == 0; }) : new List<Movie>(src);
            Comparison<Movie> byTitle = delegate(Movie a, Movie b) { return string.Compare(SortTitle(a.Title ?? ""), SortTitle(b.Title ?? ""), StringComparison.OrdinalIgnoreCase); };
            Comparison<Movie> cmp = mode == 1 ? delegate(Movie a, Movie b) { int c = b.AddedAt.CompareTo(a.AddedAt); return c != 0 ? c : byTitle(a, b); }
                : mode == 2 ? delegate(Movie a, Movie b) { int c = b.Year.CompareTo(a.Year); return c != 0 ? c : byTitle(a, b); }
                : mode == 3 ? delegate(Movie a, Movie b) { int c = (a.Year > 0 ? a.Year : 9999).CompareTo(b.Year > 0 ? b.Year : 9999); return c != 0 ? c : byTitle(a, b); }
                : mode == 4 ? delegate(Movie a, Movie b) { int c = (a.Duration > 0 ? a.Duration : long.MaxValue).CompareTo(b.Duration > 0 ? b.Duration : long.MaxValue); return c != 0 ? c : byTitle(a, b); }
                : byTitle;
            v.Sort(cmp);
            if (tv) { svSrcS = src; svCountS = src.Count; svKeyS = key; svViewS = v; }
            else { svSrcM = src; svCountM = src.Count; svKeyM = key; svViewM = v; }
            return v;
        }

        string SortLabel()
        {
            bool tv = scr == Screen2.Shows;
            return SortShort[SortModeOf(tv)] + (UnwatchedOf(tv) ? " \u00b7 Unwatched" : "");
        }

        // Rows: the sorts, then All, then Unwatched only.
        bool SortKey(Keys k, bool back)
        {
            int n = SortNames.Length + 2;
            bool tv = scr == Screen2.Shows;
            if (back) { sortOpen = false; return true; }
            if (k == Keys.Up) sortIdx = Math.Max(0, sortIdx - 1);
            else if (k == Keys.Down) sortIdx = Math.Min(n - 1, sortIdx + 1);
            else if (k == Keys.Enter || k == Keys.Space)
            {
                if (sortIdx < SortNames.Length) SetIni(SortKeyName(tv), sortIdx.ToString(CultureInfo.InvariantCulture));
                else SetIni(tv ? "UnwatchedShows" : "UnwatchedMovies", sortIdx == SortNames.Length + 1 ? "1" : "0");
                GIdx = 0; GScroll = 0;
                sortOpen = false;
            }
            return true;
        }

        void DrawSortMenu(Graphics g)
        {
            bool tv = scr == Screen2.Shows;
            int mode = SortModeOf(tv);
            bool uw = UnwatchedOf(tv);
            const float w = 640, rowH = 54;
            float h = 70 + SortNames.Length * rowH + 50 + 2 * rowH + 30;
            float x = 96, y = 120;
            RectangleF card = new RectangleF(x, y, w, h);
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, card);
            using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, x, y, w, 5);
            using (Pen p = new Pen(Color.White, 2)) g.DrawRectangle(p, x, y, w, h);
            Txt(g, "Sort by", 24, true, Color.White, x + 32, y + 22);
            float ry = y + 70;
            for (int i = 0; i < SortNames.Length + 2; i++)
            {
                if (i == SortNames.Length) { Txt(g, "Show", 24, true, Color.White, x + 32, ry + 8); ry += 50; }
                string name = i < SortNames.Length ? SortNames[i] : (i == SortNames.Length ? "All" : "Unwatched only");
                bool cur = i < SortNames.Length ? i == mode : (i == SortNames.Length + 1) == uw;
                RectangleF r = new RectangleF(x + 16, ry, w - 32, rowH - 6);
                if (cur) Txt(g, "\u25CF", 18, false, Yellow, r.X + 14, ry + 12);
                Txt(g, name, 22, i == sortIdx, i == sortIdx ? Color.White : Color.FromArgb(220, 222, 226), r.X + 44, ry + 10);
                { int ii = i; Hit(r, delegate { sortIdx = ii; }); }
                if (i == sortIdx) Focus(g, r);
                ry += rowH;
            }
        }
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

        // ---- 2.89.1: Settings > About: the version and build, this PC and its connections,
        // read when it opens (the Plex server's version is asked for in the background).
        bool aboutOn;
        List<string[]> aboutRows = new List<string[]>();   // { heading } or { label, value }
        string aboutStatus = "";

        bool IsAboutRow()
        {
            string[] r = SettingsRows();
            return setIdx >= 0 && setIdx < r.Length && r[setIdx].StartsWith("About\t", StringComparison.Ordinal);
        }

        // 1.1.0: Low latency (Video Processor), on unless turned off: the capture filter stamps each
        // frame with the playback clock's time when it arrived (plus two thirds of a frame), so it
        // is shown within a refresh instead of on a schedule set at the start. No frame is dropped.
        // Engine: CLIPGUARD_LOW_LATENCY=1, read for every frame. ini [MediaPlayer] LowLatency.
        bool LowLatency() { return Ini("LowLatency", "1") == "1"; }
        void ApplyLowLatency()
        {
            try { Environment.SetEnvironmentVariable("CLIPGUARD_LOW_LATENCY", LowLatency() ? "1" : null); } catch (Exception) { }
        }
        bool IsLowLatencyRow()
        {
            string[] r = SettingsRows();
            return setIdx >= 0 && setIdx < r.Length && r[setIdx].StartsWith("Low latency\t", StringComparison.Ordinal);
        }

#if PLAYER
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MemStatus
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemStatus));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MemStatus m);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        }
        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

        static int PhysicalCores()
        {
            try
            {
                uint len = 0;
                GetLogicalProcessorInformation(IntPtr.Zero, ref len);
                if (len == 0) return 0;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetLogicalProcessorInformation(buf, ref len)) return 0;
                    int size = IntPtr.Size == 8 ? 32 : 24, n = 0;
                    for (int off = 0; off + size <= len; off += size)
                        if (Marshal.ReadInt32(buf, off + IntPtr.Size) == 0) n++;   // RelationProcessorCore
                    return n;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception) { return 0; }
        }

        static string RegStr(RegistryKey k, string name) { try { object v = k == null ? null : k.GetValue(name); return v == null ? "" : v.ToString(); } catch (Exception) { return ""; } }

        static string WindowsText()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = RegStr(k, "ProductName"), disp = RegStr(k, "DisplayVersion"), build = RegStr(k, "CurrentBuild"), ubr = RegStr(k, "UBR");
                    int b; if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return (name + (disp.Length > 0 ? " " + disp : "") + "  (build " + build + (ubr.Length > 0 ? "." + ubr : "") + ")").Trim();
                }
            }
            catch (Exception) { return Environment.OSVersion.VersionString; }
        }

        static List<string> GpuTexts()
        {
            List<string> l = new List<string>();
            try
            {
                using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey k = cls.OpenSubKey(sub))
                        {
                            string name = RegStr(k, "DriverDesc"), ver = RegStr(k, "DriverVersion");
                            if (name.Length == 0 || name.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            string drv = ver;
                            if (name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // 32.0.15.6214 -> 562.14
                                string digits = ver.Replace(".", "");
                                if (digits.Length >= 5) { string d5 = digits.Substring(digits.Length - 5); drv = d5.Substring(0, 3) + "." + d5.Substring(3) + "  (" + ver + ")"; }
                            }
                            string mem = "";
                            object q = null;
                            try { q = k.GetValue("HardwareInformation.qwMemorySize"); } catch (Exception) { }
                            if (q is long && (long)q > 0) mem = ", " + ((long)q / 1073741824.0).ToString("0", CultureInfo.InvariantCulture) + " GB";
                            else if (q is byte[] && ((byte[])q).Length == 8) { long v = BitConverter.ToInt64((byte[])q, 0); if (v > 0) mem = ", " + (v / 1073741824.0).ToString("0", CultureInfo.InvariantCulture) + " GB"; }
                            string t = name + mem + "  ·  driver " + drv;
                            if (!l.Contains(t)) l.Add(t);
                        }
                    }
            }
            catch (Exception) { }
            if (l.Count == 0) l.Add("not found");
            return l;
        }

        static string FileVer(string path)
        {
            try
            {
                if (!File.Exists(path)) return "not found";
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
                string s2 = (v.ProductVersion ?? v.FileVersion ?? "").Trim();
                return (s2.Length > 0 ? s2 : "present") + "  (" + File.GetLastWriteTime(path).ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception) { return "?"; }
        }

        void AboutOpen()
        {
            List<string[]> r = new List<string[]>();
            r.Add(new[] { "CLIPGUARD MEDIA PLAYER" });
#if STANDALONE
            string ver = PlayerProgram.MediaPlayerVersion;
#else
            string ver = "?";
#endif
            r.Add(new[] { "Version", ver });
            string bf = Path.Combine(PlayerDir, "installed_build.txt");
            string build = "not recorded";
            try { if (File.Exists(bf)) build = "run " + File.ReadAllText(bf).Trim() + ", installed " + File.GetLastWriteTime(bf).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture); } catch (Exception) { }
            r.Add(new[] { "Build", build });
            r.Add(new[] { "Installed in", PlayerDir });
            string filters = Path.Combine(PlayerDir, "filters");
            r.Add(new[] { "Renderer", FileVer(Path.Combine(filters, "unplugged_vr.ax")) });
            r.Add(new[] { "Engine", FileVer(Path.Combine(PlayerDir, "s3engine.dll")) });
            r.Add(new[] { "LAV Filters", FileVer(Path.Combine(filters, "LAVVideo.ax")) });
            r.Add(new[] { "ArgyllCMS", File.Exists(Path.Combine(PlayerDir, "spotread.exe")) ? "included  (" + File.GetLastWriteTime(Path.Combine(PlayerDir, "spotread.exe")).ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ")" : "not found" });

            r.Add(new[] { "THIS PC" });
            r.Add(new[] { "Computer", Environment.MachineName });
            r.Add(new[] { "Windows", WindowsText() });
            string cpu = "";
            try { using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) cpu = RegStr(k, "ProcessorNameString").Trim(); } catch (Exception) { }
            int cores = PhysicalCores();
            r.Add(new[] { "Processor", (cpu.Length > 0 ? cpu : "?") + "  ·  " + (cores > 0 ? cores + " cores / " : "") + Environment.ProcessorCount + " threads" });
            MemStatus ms = new MemStatus();
            string ram = "?";
            try { if (GlobalMemoryStatusEx(ms)) ram = (ms.ullTotalPhys / 1073741824.0).ToString("0", CultureInfo.InvariantCulture) + " GB  (" + ms.dwMemoryLoad + "% in use)"; } catch (Exception) { }
            r.Add(new[] { "Memory", ram });
            List<string> gpus = GpuTexts();
            for (int i = 0; i < gpus.Count; i++) r.Add(new[] { i == 0 ? "Graphics" : "", gpus[i] });
            try
            {
                Screen sc = Screen.FromControl(this);
                DevMode dm = new DevMode(); dm.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
                string mode = sc.Bounds.Width + " x " + sc.Bounds.Height;
                if (EnumDisplaySettings(sc.DeviceName, -1, ref dm)) mode = dm.dmPelsWidth + " x " + dm.dmPelsHeight + " at " + dm.dmDisplayFrequency + " Hz";
                r.Add(new[] { "Display", mode + "  (" + sc.DeviceName.Replace(@"\\.\", "") + (sc.Primary ? ", main" : "") + ")" });
            }
            catch (Exception) { }

            r.Add(new[] { "CONNECTIONS" });
            int pi = r.Count;
            r.Add(new[] { "Plex server", servers.Count == 0 ? "none found" : "asking..." });
            aboutRows = r;
            aboutStatus = "";
            aboutOn = true;
            Invalidate();
            if (servers.Count > 0)
            {
                List<Server> ss = new List<Server>(servers);
                List<string> got = new List<string>();
                Bg2(delegate
                {
                    foreach (Server sv in ss)
                    {
                        string v = "";
                        try { v = Plex.Str(Plex.Get(Plex.Json(Plex.Http("GET", Url(sv, "/identity"), sv.Token, 5000)), "MediaContainer"), "version"); } catch (Exception) { v = "not answering"; }
                        got.Add(sv.Name + "  ·  " + (v.Length > 0 ? "Plex Media Server " + v : "?") + (sv.Owned ? "" : "  (shared with you)"));
                    }
                }, delegate
                {
                    if (!aboutOn || aboutRows != r) return;
                    r[pi] = new[] { "Plex server", got.Count > 0 ? got[0] : "?" };
                    for (int i = 1; i < got.Count; i++) r.Insert(pi + i, new[] { "", got[i] });
                    Invalidate();
                });
            }
        }

        string AboutText()
        {
            StringBuilder b = new StringBuilder();
            foreach (string[] a in aboutRows)
            {
                if (a.Length == 1) { if (b.Length > 0) b.Append("\r\n"); b.Append(a[0]).Append("\r\n"); }
                else b.Append(a[0].Length > 0 ? a[0] + ": " : "    ").Append(a[1]).Append("\r\n");
            }
            return b.ToString();
        }

        bool AboutKey(Keys k)
        {
            if (k == Keys.Enter || k == Keys.C || k == Keys.Right)
            {
                try { Clipboard.SetText(AboutText()); aboutStatus = "Copied to the clipboard."; } catch (Exception) { aboutStatus = "Couldn't copy to the clipboard."; }
            }
            else if (k == Keys.Escape || k == Keys.Back || k == Keys.BrowserBack || k == Keys.Left) aboutOn = false;
            Invalidate();
            return true;
        }

        void DrawAbout(Graphics g)
        {
            RectangleF r = new RectangleF(200, 90, 1520, 900);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(250, 30, 32, 38))) g.FillRectangle(b, r);
            using (SolidBrush y = new SolidBrush(Yellow)) g.FillRectangle(y, r.X, r.Y, r.Width, 5);
            Txt(g, "About", 30, true, Color.White, r.X + 40, r.Y + 26);
            float yy = r.Y + 88;
            foreach (string[] a in aboutRows)
            {
                if (yy > r.Bottom - 90) break;
                if (a.Length == 1)
                {
                    yy += 8;
                    Txt(g, a[0], 18, true, Yellow, r.X + 40, yy);
                    yy += 32;
                    continue;
                }
                Txt(g, a[0], 20, false, Grey, r.X + 40, yy);
                TextBox(g, a[1], 20, false, Color.White, new RectangleF(r.X + 300, yy, r.Width - 340, 30));
                yy += 31;
            }
            Txt(g, "OK: copy to the clipboard (for a GitHub issue)      Back: close" + (aboutStatus.Length > 0 ? "      " + aboutStatus : ""), 20, false, Grey, r.X + 40, r.Bottom - 48);
        }
#else
        void AboutOpen() { }
        bool AboutKey(Keys k) { aboutOn = false; return true; }
        void DrawAbout(Graphics g) { }
#endif

        string[] SettingsRows()
        {
#if !PLAYER
            int fr = host.LookAheadFrames();
#endif
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
                "Start with Windows\t" + (StartWithWindows() ? "On" : "Off") + "   (opens the player when you sign in to Windows; Left / Right)",
                "Theme\t" + ThemeNames[themeIdx] + "   (Left / Right)",
                "Menu sounds\t" + new[] { "Off", "Soft", "Normal" }[MenuSoundLevel()] + "   (Left / Right)"
                , "Low latency\t" + (LowLatency() ? "On" : "Off") + "   (Video Processor, for lip sync; Left / Right)"   // 1.1.0
                , "About\tVersion, build and this PC   (Enter)"   // 2.89.1
                };
#else
                "Back to Video Processor\tPress Enter   (F4)" };
#endif
        }

        // 2.71.0: the row's meaning: the player has no "Audio delay now" row (row 1), so the
        // rows after it are one place higher on screen than their number here.
#if PLAYER
        // 0.9.1: rows 10 and 11 are not used: Menu sounds is the 11th row shown.
        int SetRow() { int r = setIdx >= 1 ? setIdx + 1 : setIdx; return r >= 10 ? r + 2 : r; }
#else
        int SetRow() { return setIdx; }
#endif

#if PLAYER

        // ---- 2.86.0: the film's file details for the top banner (FILE column) -------------
        static readonly InfoShared fileInfo = new InfoShared(@"Local\ClipGuard_FileInfo");

        void FileInfoTick()
        {
            if (player != null) return;   // 0.9.1: no FILE column in the public build
            try
            {
                if (player == null || capturing || playing == null) return;
                fileInfo.Write(string.Join("\n", FileInfoLines().ToArray()));
            }
            catch (Exception) { }
        }

        static string Mbps(long kbps) { return (kbps / 1000.0).ToString(kbps >= 100000 ? "0" : "0.0", CultureInfo.InvariantCulture) + " Mbps"; }

        List<string> FileInfoLines()
        {
            Movie m = playing;
            Ver v = curVer;
            List<string> l = new List<string>();
            string title = m.Type == "episode" && m.ShowTitle.Length > 0 ? m.ShowTitle + " " + m.Code : m.Title + (m.Year > 0 ? " (" + m.Year.ToString(CultureInfo.InvariantCulture) + ")" : "");
            l.Add(InfoShared.Ascii(title));
            if (v == null) return l;
            string name = v.File.Length > 0 ? Path.GetFileName(v.File.Replace('/', '\\')) : "";
            if (name.Length > 48) name = name.Substring(0, 45) + "...";
            if (name.Length > 0) l.Add(InfoShared.Ascii(name));
            List<string> cs = new List<string>();
            if (v.Container.Length > 0) cs.Add(v.Container.ToUpperInvariant());
            if (v.Size > 0) cs.Add((v.Size / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB");
            if (cs.Count > 0) l.Add(string.Join(" \u00B7 ", cs.ToArray()));
            if (v.Bitrate > 0) l.Add(Mbps(v.Bitrate) + " overall" + (v.VBitrate > 0 ? ", " + Mbps(v.VBitrate) + " video" : ""));
            List<string> vp = new List<string>();
            string codec = v.VCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "HEVC" : v.VCodec.Equals("h264", StringComparison.OrdinalIgnoreCase) ? "H.264" : v.VCodec.ToUpperInvariant();
            if (codec.Length > 0) vp.Add((codec + " " + (v.VProfile.Length > 0 ? char.ToUpperInvariant(v.VProfile[0]) + v.VProfile.Substring(1) : "")).Trim());
            if (v.Dovi.Length > 0) vp.Add("Dolby Vision profile " + v.Dovi);
            else if (v.Hdr.Length > 0 && v.Hdr != "SDR") vp.Add(v.Hdr);
            if (vp.Count > 0) l.Add(InfoShared.Ascii(string.Join(", ", vp.ToArray())));
            int a = AudioNow();
            string au = a >= 0 && a < audioNames.Count ? audioNames[a] : v.Audio;
            if (au.Length > 0) l.Add("Audio: " + InfoShared.Ascii(au.Replace("  \u00b7  ", " \u00B7 ")));
            int sb = SubNow();
            string su = sb >= 0 && sb < subNames.Count ? subNames[sb] : "Off";
            l.Add("Subtitles: " + InfoShared.Ascii(su.Replace("  \u00b7  ", " \u00B7 ")));
            if (m.Edition.Length > 0) l.Add("Edition: " + InfoShared.Ascii(m.Edition));
            return l;
        }

        // ---- 2.86.0: menu sounds (library and settings only, never in a film or capture) -------
        // Short tones made here (no files): a tick to move, a rising click to select, a falling
        // one to go back. Settings > Menu sounds: Off, Soft, Normal (ini MenuSounds 0 / 1 / 2).
        int MenuSoundLevel() { int v; return int.TryParse(Ini("MenuSounds", "1"), out v) ? Math.Max(0, Math.Min(2, v)) : 1; }
        readonly Dictionary<string, byte[]> soundCache = new Dictionary<string, byte[]>();

        // 2.86.1: a silent stream runs while the menus are on screen, so the HDMI audio link
        // to the receiver stays locked and the short tones are heard; it stops for a film or
        // Video Processor, leaving the device to the video engine.
        readonly MenuAudio menuAudio = new MenuAudio();

        void MenuAudioTick()
        {
            bool want = player == null && !capturing && Visible && MenuSoundLevel() > 0 && scr != Screen2.Starting;
            if (want) menuAudio.Start(); else menuAudio.Stop();
        }

        void MenuSound(Keys k)
        {
            try
            {
                if (player != null || capturing || learning) return;
                int lvl = MenuSoundLevel();
                if (lvl == 0) return;
                string kind = (k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right) ? "move"
                    : (k == Keys.Enter || k == Keys.Space) ? "select"
                    : (k == Keys.Escape || k == Keys.Back || k == Keys.BrowserBack || k == Keys.F4) ? "back" : null;
                if (kind == null) return;
                string key = kind + lvl.ToString(CultureInfo.InvariantCulture);
                byte[] wav;
                if (!soundCache.TryGetValue(key, out wav))
                {
                    double amp = lvl == 1 ? 0.10 : 0.25;
                    wav = kind == "move" ? Tone(1700, 1700, 0.018, amp) : kind == "select" ? Tone(900, 1350, 0.045, amp) : Tone(1100, 700, 0.045, amp);
                    soundCache[key] = wav;
                }
                menuAudio.Start();
                menuAudio.Play(wav);
            }
            catch (Exception) { }
        }

        // A sine sweep from f0 to f1 Hz, with a 2 ms fade in and an exponential fade out (16-bit mono WAV).
        static byte[] Tone(double f0, double f1, double seconds, double amp)
        {
            const int rate = 44100;
            int n = (int)(rate * seconds);
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            double ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                double f = f0 + (f1 - f0) * t;
                ph += 2 * Math.PI * f / rate;
                double env = Math.Min(1.0, i / (rate * 0.002)) * Math.Exp(-4.0 * t);
                w.Write((short)(Math.Sin(ph) * env * amp * 32767));
            }
            w.Flush();
            return ms.ToArray();
        }

#else
        void FileInfoTick() { }
        void MenuSound(Keys k) { }
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
            else if (k == Keys.Enter && IsAboutRow()) AboutOpen();   // 2.89.1
            else if ((k == Keys.Left || k == Keys.Right) && IsLowLatencyRow())
            {
                SetIni("LowLatency", LowLatency() ? "0" : "1");   // 1.1.0
                ApplyLowLatency();
            }
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 0)
            {
                unpluggedDelay = ClampDelay(unpluggedDelay + (k == Keys.Right ? 5 : -5));
                SetIni(DelayKey, unpluggedDelay.ToString(CultureInfo.InvariantCulture));
            }
#if PLAYER
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 6) SetIni("NextEpisode", Ini("NextEpisode", "1") != "0" ? "0" : "1");
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 8) SetStartWithWindows(!StartWithWindows());   // 2.73.0
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 9)
            {
                ApplyTheme((themeIdx + (k == Keys.Right ? 1 : Themes.Length - 1)) % Themes.Length);   // 2.75.0: live
                SetIni("Theme", themeIdx.ToString(CultureInfo.InvariantCulture));
                PublishView();   // 1.0.0: the renderer's banner follows the theme
                BackColor = Bg;
                if (video != null) video.BackColor = Color.Black;
            }
            else if ((k == Keys.Left || k == Keys.Right) && SetRow() == 12)
                SetIni("MenuSounds", ((MenuSoundLevel() + (k == Keys.Right ? 1 : 2)) % 3).ToString(CultureInfo.InvariantCulture));   // 2.86.0
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
                else if (SetRow() == 8) SetStartWithWindows(!StartWithWindows());
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
            delegate { if (seq != startSeq) return; EndStarting(); ViewForFilm(m); Launch(url, startMs, m, false); },   // 0.9.5: the lens fit, once per film
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
        DateTime stageAt = DateTime.Now;
        HttpWebRequest startReq;
        System.Windows.Forms.Timer startTimer;
        Screen2 startFrom = Screen2.Details;

        void BeginStarting(Movie m)
        {
            startM = m;
            startSeq++;
            startStage = 0;
            stageAt = DateTime.Now;
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
            if (art != null) DrawFit(g, art, new RectangleF(0, 0, 1920, 1080), 215, Bg);   // 0.9.4
            Poster(g, m, 0, 200, 270, 360, 540, false);
            const float x = 640, w = 1080;
            bool ep = m.Type == "episode";
            TextBox(g, ep ? m.ShowTitle : m.Title, 56, true, Color.White, new RectangleF(x, 300, w, 80));
            List<string> meta = new List<string>();
            if (ep) meta.Add(m.Code + "  " + m.Title);
            else if (m.Year > 0) meta.Add(m.Year.ToString(CultureInfo.InvariantCulture));
            if (m.Duration > 0) meta.Add(DurText(m.Duration));
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
            playStarted = DateTime.Now;   // 2.89.0
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
            ViewForFilm(null);   // 0.9.5: capture: the frame's own shape (or the override)
            if (player != null || capturing || quitting) return;   // (also from Start in, while the library still loads)
            capDevice = CaptureDevice();
            capturing = true;
            playing = null;
            lipSync = false;
            menuOpen = false;
            barUntil = DateTime.MinValue; msgUntil = DateTime.MinValue;
            capUntil = DateTime.MinValue;
            capWaiting = false; capInfo = false;
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
        DateTime capNoSigSince = DateTime.MinValue;   // 2.82.0
        bool capWaiting;          // no signal: the waiting card stays up until the picture comes
        bool capInfo;             // the card on screen is Info
        int capWaitTick;
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
            string gt = GpuTemp.Text();
            if (gt.Length > 0) r.Add(new[] { "GPU", gt });   // 1.1.0
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
            // 2.79.0: about half the size, in the bottom-right corner
            string[][] rows = InfoRows();
            const float w = 660;
            float h = 64 + rows.Length * 28 + 40;
            float x = W - w - 50, y = 1080 - h - 50;
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, x, y, w, h);
            using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, x, y, w, 4);
            Txt(g, "Info", 22, true, Color.White, x + 24, y + 16);
            Txt(g, capDevice, 15, false, Grey, x + 100, y + 22);
            float ry = y + 62;
            foreach (string[] r in rows)
            {
                Txt(g, r[0], 15, true, Grey, x + 24, ry);
                Txt(g, r[1], 15, false, Color.White, x + 150, ry);
                ry += 28;
            }
            Txt(g, CapHelp(), 13, false, Grey, x + 24, y + h - 30);
        }

        void DrawWaitCard(Graphics g, float W)
        {
            // 2.82.0: small, bottom-right, like Info
            const float w = 420, h = 64;
            float x = W - w - 50, y = 1080 - h - 50;
            using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, x, y, w, h);
            using (SolidBrush yb = new SolidBrush(Yellow)) g.FillRectangle(yb, x, y, w, 4);
            Txt(g, "Waiting for signal", 20, true, Color.White, x + 24, y + 18);
            for (int i = 0; i < 3; i++)
                using (SolidBrush db = new SolidBrush(i == capWaitTick % 3 ? Yellow : Dim)) g.FillEllipse(db, x + 300 + i * 22, y + 28, 11, 11);
        }

        string CapHelp()
        {
            return "F7  ClipGuard      I  info      Back  library";
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
            Txt(g, CapHelp(), 20, false, Grey, x + 40, y + 136);
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
            // 2.82.0: no Info on its own (I or Home shows it); Waiting only after 3 s without a picture.
            if (locked == 0 && capNoSigSince == DateTime.MinValue) capNoSigSince = DateTime.Now;
            if (locked != 0) capNoSigSince = DateTime.MinValue;
            if (locked == 0 && (DateTime.Now - capStarted).TotalSeconds > 3 && (DateTime.Now - capNoSigSince).TotalSeconds > 3)
            {
                capWaiting = true; capInfo = false; capWaitTick++;
                capUntil = DateTime.Now.AddMilliseconds(2500);
                Kick();
            }
            else if (locked == 1)
            {
                if (capWaiting) { capWaiting = false; capUntil = DateTime.Now; Kick(); }
            }
            FrmCaptureTick();   // 2.65.0: frame rate matching
            PublishCaptureAudio();   // 2.69.0: follows look-ahead changes (F7)
        }

        bool CaptureKey(Keys k, bool ctrl)
        {
            if (ctrl && k == Keys.S) { StopCapture(); return true; }
            if (ctrl && k == Keys.M) { StopCapture(); return true; }               // 2.74.0: Media Player button
            if (ctrl && k == Keys.V) { if (capWaiting) Kick(); else ShowInfo(3000); return true; }   // already here
            if (ctrl) return false;
            switch (k)
            {
                case Keys.Escape: case Keys.F4: case Keys.Back: case Keys.BrowserBack: case Keys.MediaStop: StopCapture(); return true;   // 2.73.0: Esc is Back
                case Keys.I: if (capWaiting) Kick(); else if (capInfo) { capInfo = false; capUntil = DateTime.Now; Kick(); } else ShowInfo(3000); return true;   // 2.79.0: I again hides it   // 2.70.0: Info
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
            Txt(g, "While it runs:  F7 ClipGuard menu,   I  info,   Media Player button (Ctrl+M) or F4  library.", 22, false, Grey, 96, 580);
        }


#if PLAYER
        // 2.83.0: the Home button is F6: ClipGuard's info banner at the top (films and Video Processor).
        void BannerKey()
        {
            try { if (player != null && PHost != null) PHost.ToggleClipGuardBanner(); } catch (Exception) { }
        }
#endif

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
                else if (key == "!ssr") { if (PHost != null) PHost.SetSsr(!PHost.Ssr()); }   // 2.85.0
                else if (key == "!gamma") { if (PHost != null) PHost.SetOutGamma((PHost.OutGamma() + (dir < 0 ? 2 : 1)) % 3); }   // 2.88.1
                else if (key == "!depth") { if (PHost != null) PHost.SetDepthLevel(PHost.DepthLevel() + dir * 5); }   // 2.94.0
                else if (key == "!calrun") { if (k == Keys.Enter) CalBegin(); }
#if PLAYER
                else if (key == "!clipbit") { SetIni("ClipBit", ((ClipBitMode() + 6 + dir) % 6).ToString(CultureInfo.InvariantCulture)); PublishView(); }   // 0.9.6
                else if (key == "!cbdetail") { SetIni("BitrateDetail", Math.Max(0, Math.Min(100, DetDetail() + dir * 5)).ToString(CultureInfo.InvariantCulture)); PublishView(); }
                else if (key == "!cbgrain") { SetIni("BitrateGrain", Math.Max(0, Math.Min(100, DetGrain() + dir * 5)).ToString(CultureInfo.InvariantCulture)); PublishView(); }
                else if (key == "!lensup")   // 0.9.6: Lightweight / Standard / Neural 2x first
                {
                    int[] order = { 2, 0, 1 };
                    int at = Array.IndexOf(order, LensUp());
                    SetIni("LensUpscale", order[((at + dir) % 3 + 3) % 3].ToString(CultureInfo.InvariantCulture)); PublishView();
                }
                else if (key == "!ravu") { SetIni("Ravu", Ini("Ravu", "1") == "1" ? "0" : "1"); PublishView(); }
                else if (key == "!lens") { int i = Array.IndexOf(LensVals, scrProf.Lens); scrProf.Lens = LensVals[(Math.Max(0, i) + LensVals.Length + dir) % LensVals.Length]; SaveScreenProfile(); if (ovl != null) ovl.Invalidate(); }   // 0.9.5
                else if (key == "!scope") { scrProf.Screen = scrProf.Screen == 235 ? 240 : 235; SaveScreenProfile(); }
                else if (key == "!shape") { scrProf.Mode = (scrProf.Mode + 3 + dir) % 3; SaveScreenProfile(); }
                else if (key == "!geodef") { if (k == Keys.Enter) GeoEditStart(); }
                else if (key == "!geo") { scrProf.GeoOn = !scrProf.GeoOn; SaveScreenProfile(); }
                else if (key == "!georeset") { if (k == Keys.Enter) { scrProf.Pt = new int[24]; SaveScreenProfile(); message = "Screen geometry reset."; } }
#endif
                else if (key == "!lut") LutStep(dir);
                else if (key == "!caldir") { }
                else if (key == "!copy")
                {
                    if (k == Keys.Enter)
                        message = UnpluggedSettings.ImportFromUnplugged()
                            ? "Picture settings copied again from Unplugged."
                            : "Unplugged's settings were not found, so nothing was copied.";
                }
                else
                {
                    UnpluggedSettings.PictureStep(rows[picIdx], dir);
                    if (key == "hdr.outputGamut" && Ini("Bt2020Flag", "1") != "0")   // 2.108.0: the flag follows it at once
                    {
                        string dev = FrmDevice();
                        bool bt2020 = UnpluggedSettings.Output2020();
                        ThreadPool.QueueUserWorkItem(delegate { NvRange.SendColorimetry(dev, bt2020); });
                    }
                }
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
            if (key == "!ssr") return PHost != null && PHost.Ssr() ? "On" : "Off";   // 2.85.0
            if (key == "!gamma") return PHost == null ? "2.2" : new[] { "2.2", "2.4", "BT.1886" }[Math.Max(0, Math.Min(2, PHost.OutGamma()))];   // 2.88.1
            if (key == "!depth") return PHost == null ? "-" : (PHost.DepthLevel() == 0 ? "Off" : PHost.DepthLevel().ToString(CultureInfo.InvariantCulture));   // 2.94.0
            if (key == "!copy") return "Press Enter";
            if (key == "!calrun") return "Press Enter";
#if PLAYER
            if (key == "!clipbit") return ClipBitNames[ClipBitMode()];   // 0.9.6
            if (key == "!cbdetail") return DetDetail() == 0 ? "Off" : DetDetail().ToString(CultureInfo.InvariantCulture);
            if (key == "!cbgrain") return DetGrain() == 0 ? "Off" : DetGrain().ToString(CultureInfo.InvariantCulture);
            if (key == "!lensup") { int lu = LensUp(); return lu == 1 ? "Neural 2\u00d7 first" : (lu == 2 ? "Lightweight" : "Standard"); }
            if (key == "!ravu") return Ini("Ravu", "1") == "1" ? "On" : "Off";
            if (key == "!lens") return scrProf.Lens == 0 ? "Off" : (scrProf.Lens / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "\u00d7";   // 0.9.5
            if (key == "!scope") return (scrProf.Screen / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            if (key == "!shape") return new[] { "Auto", "Scope", "Not scope" }[scrProf.Mode];
            if (key == "!geodef") return "Press Enter";
            if (key == "!geo") return scrProf.GeoOn ? "On" : "Off";
            if (key == "!georeset") return "Press Enter";
#endif
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
                    { int ii = i; Hit(rr, delegate { navFocus = false; picIdx = ii; }); }
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
                { int ii = i; Hit(rr, delegate { navFocus = false; picIdx = ii; }); }
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

        // True when the rate was changed (the projector and receiver re-sync).
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
                    if (rc == 0) { frmChanged = true; frmHz = hz; Bt2020Later(dev); return true; }
                    return false;
                }
            }
            return false;
        }

        void FrmRestore()
        {
            if (!frmChanged) return;
            frmChanged = false; frmHz = 0;
            string dev = FrmDevice();
            try { ChangeDisplaySettingsExW(dev, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); } catch (Exception) { }
            Bt2020Later(dev);   // 2.106.2
        }

        // 2.106.2: a mode change can reset the colour data: the BT.2020 flag again once the link is back.
        void Bt2020Later(string dev)
        {
            if (Ini("Bt2020Flag", "1") == "0") return;
            bool bt2020 = UnpluggedSettings.Output2020();   // 2.108.0
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(2500); NvRange.SendColorimetry(dev, bt2020); });
        }

        // 2.106.1: the refresh-rate card near the bottom of the picture: "Changing to 23.976 Hz"
        // while the projector re-locks, then the rate on its own for a moment once the film plays.
        string rateHz = "";
        bool rateChanging;
        DateTime rateUntil = DateTime.MinValue;
        static string HzLabel(int hz)
        {
            switch (hz)
            {
                case 23: return "23.976";
                case 29: return "29.97";
                case 59: return "59.94";
                default: return hz.ToString(CultureInfo.InvariantCulture) + ".00";
            }
        }
        // 0.9.6: ClipBit: Detail and Grain for 4K films under the bitrate limit (Settings).
        // Mode: 0 Off, 1-4 under 15 / 20 / 25 / 30 Mbps, 5 Always (4K still required).
        int DetDetail() { return Math.Max(0, Math.Min(100, IniInt("BitrateDetail", 80))); }
        int DetGrain() { return Math.Max(0, Math.Min(100, IniInt("BitrateGrain", 50))); }
        static readonly int[] ClipBitLimits = { 0, 15, 20, 25, 30, 0 };
        static readonly string[] ClipBitNames = { "Off", "Under 15 Mbps", "Under 20 Mbps", "Under 25 Mbps", "Under 30 Mbps", "Always" };
        int ChromaCg() { return Math.Max(0, Math.Min(2, IniInt("ChromaGuided", 0))); }
        int SsimDown() { return IniInt("SsimDown", 0) == 1 ? 1 : 0; }
        int LensUp() { int v = IniInt("LensUpscale", 0); return v == 1 || v == 2 ? v : 0; }
        int ClipBitMode() { return Math.Max(0, Math.Min(5, IniInt("ClipBit", 3))); }
        long ClipBitKbps() { Ver v = curVer; return v == null ? 0 : (v.VBitrate > 0 ? v.VBitrate : v.Bitrate); }
        bool ClipBit4K() { Ver v = curVer; return v != null && v.Res == "4K"; }
        int ClipBitState()
        {
            if (!ClipBit4K()) return 0;
            int m = ClipBitMode();
            if (m == 0) return 0;
            if (m == 5) return 1;
            long kb = ClipBitKbps();
            return kb > 0 && kb < ClipBitLimits[m] * 1000L ? 1 : 0;
        }
        int IniInt(string k, int d)
        {
            int v;
            return int.TryParse(Ini(k, d.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : d;
        }

        void RateCard(bool changing)
        {
            rateHz = HzLabel(frmHz);
            rateChanging = changing;
            rateUntil = DateTime.Now.AddMilliseconds(changing ? 6000 : 2500);
            Kick();
        }
        void DrawRateCard(Graphics g, float W)
        {
            string lead = rateChanging ? "Changing to " : "";
            Font fl = FontC(30, true), fv = FontC(30, true), ft = FontC(18, true);
            SizeF zl = lead.Length > 0 ? g.MeasureString(lead, fl) : SizeF.Empty;
            SizeF zv = g.MeasureString(rateHz + " Hz", fv);
            float w = zl.Width + zv.Width + 72, h = 96;
            float x = (W - w) / 2, y = BarOn() ? 1080 - 250 - h - 24 : 1080 - h - 70;
            using (SolidBrush b = new SolidBrush(Bg)) g.FillRectangle(b, x, y, w, h);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 73, 80))) g.FillRectangle(b, x, y, w, 2);
            using (Pen p = new Pen(Color.White, 2)) g.DrawRectangle(p, x, y, w, h);
            using (SolidBrush b = new SolidBrush(Grey)) g.DrawString("FRAME RATE", ft, b, x + 36, y + 12);
            float tx = x + 36, ty = y + 40;
            if (lead.Length > 0) using (SolidBrush b = new SolidBrush(Color.White)) g.DrawString(lead, fl, b, tx, ty);
            using (SolidBrush b = new SolidBrush(Yellow)) g.DrawString(rateHz + " Hz", fv, b, tx + zl.Width, ty);
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

        // A film: match once, with the film held while the projector and receiver re-sync.
        void FrmFilmTick(string st)
        {
            if (frmResumeAt != DateTime.MaxValue)
            {
                if (DateTime.Now >= frmResumeAt)
                {
                    frmResumeAt = DateTime.MaxValue;
                    RateCard(false);   // 2.106.1
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
                RateCard(true);   // 2.106.1
            }
        }

        // Video Processor: follow the source when its frame rate changes.
        void FrmCaptureTick()
        {
            double fps = 0;
            try { fps = EngineFps(true); } catch (Exception) { }
            if (fps <= 1 || Math.Abs(fps - capFpsLast) < 0.01) return;
            capFpsLast = fps;
            FrmApply(fps);   // 2.82.0: no Info on its own   // 2.70.0: Info shows the projector's new rate
        }

        // ---- 2.66.0: Calibrate a projector (any projector, by name) --------------------------
        // The meter (ClipGuardMeter.exe in the Player folder) does the run with the player's own
        // ArgyllCMS; the .ccss, the LUT and the report are in the Calibration folder.
        public static string CalDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Calibration"); } }
        static string PlayerDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }
        int calState;            // 0 none, 1 typing the projector's name, 2 choosing the correction
        string calName = "";
        // 2.88.1: the correction is any .ccss in the Calibration folder, or none ("" last).
        List<string> calCcss = new List<string>();
        int calCcssIdx;

        void CalLoadCcss()
        {
            calCcss = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(CalDir, "*.ccss")) calCcss.Add(Path.GetFileName(f));
                calCcss.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { }
            calCcss.Add("");   // none
            // the one named after the projector first, else the first file
            string mine = SafeName(calName) + ".ccss";
            int i = calCcss.FindIndex(n => string.Equals(n, mine, StringComparison.OrdinalIgnoreCase));
            calCcssIdx = i >= 0 ? i : 0;
        }

        string CalCcssChoice() { return calCcssIdx < calCcss.Count && calCcss[calCcssIdx].Length > 0 ? Path.Combine(CalDir, calCcss[calCcssIdx]) : ""; }

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
                    CalLoadCcss();
                }
                else
                {
                    char ch = KeyChar(k);
                    if (ch != '\0' && calName.Length < 48) calName += ch;
                }
            }
            else if (calState == 2)
            {
                int n = Math.Max(1, calCcss.Count);
                if (c == Keys.Right || c == Keys.Down) calCcssIdx = (calCcssIdx + 1) % n;
                else if (c == Keys.Left || c == Keys.Up) calCcssIdx = (calCcssIdx + n - 1) % n;
                else if (c == Keys.Back) calState = 1;
                else if (c == Keys.Enter)
                {
                    string f = CalCcssChoice();
                    if (f.Length > 0 && !File.Exists(f)) { CalLoadCcss(); Invalidate(); return true; }   // gone meanwhile
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
            string ccssArg = CalCcssChoice();
            string args = "--cal \"" + calName.Replace("\"", "") + "\" --ccss \"" + (ccssArg.Length > 0 ? ccssArg : "none")
                + "\" --out \"" + CalDir + "\" --argyll \"" + PlayerDir + "\" --screen \"" + UnpluggedScreen().DeviceName + "\"";
            Process p;
            try { p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = PlayerDir }); }
            catch (Exception ex) { message = "The calibration could not start: " + ex.Message; Invalidate(); return; }
            Hide();
            Thread t = new Thread(delegate()
            {
                try { p.WaitForExit(); } catch (Exception) { }
                BeginInvoke((MethodInvoker)delegate
                {
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
                Txt(g, "Projector:  " + calName, 26, true, Color.White, r.X + 40, r.Y + 95);
                int nFiles = calCcss.Count - 1;
                Txt(g, nFiles > 0 ? "Correction (Left / Right: " + nFiles.ToString(CultureInfo.InvariantCulture) + (nFiles == 1 ? " file" : " files") + " in the folder, or none):"
                    : "Correction: no .ccss files in the folder", 24, false, Grey, r.X + 40, r.Y + 150);
                string cur = calCcssIdx < calCcss.Count ? calCcss[calCcssIdx] : "";
                RectangleF o = new RectangleF(r.X + 40, r.Y + 195, r.Width - 80, 64);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, o);
                Txt(g, "<", 28, true, Grey, o.X + 18, o.Y + 12);
                Txt(g, cur.Length > 0 ? cur : "None (the meter's standard reading)", 26, true, Color.White, o.X + 56, o.Y + 12);
                Txt(g, ">", 28, true, Grey, o.Right - 40, o.Y + 12);
                Focus(g, o);
                Txt(g, (calCcssIdx + 1).ToString(CultureInfo.InvariantCulture) + " of " + calCcss.Count.ToString(CultureInfo.InvariantCulture), 20, false, Grey, o.X, o.Bottom + 10);
                string note = cur.Length > 0
                    ? "Enter starts the calibration with " + cur + ". More: put .ccss files in " + CalDir + "."
                    : "No .ccss: accuracy depends on how well the meter matches this projector's light. Enter starts. (.ccss files go in " + CalDir + ".)";
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
                { int ii = i; Hit(rr, delegate { navFocus = false; cgIdx = ii; }); }
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
            if (art != null) DrawFit(g, art, new RectangleF(0, 0, 1920, 1080), 215, Bg);   // 0.9.4
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
                    { int ii = i; Hit(new RectangleF(sx - 4, 194, sz.Width + 8, 40), delegate { onSeasons = true; for (int n = 0; n < 64 && seasonIdx != ii; n++) ShowKey(ii > seasonIdx ? Keys.Right : Keys.Left, false); }); }
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
                string right = e.ViewCount > 0 && e.Offset == 0 ? "Watched" : (e.Offset > 0 && e.Duration > 0 ? DurText(e.Duration - e.Offset) + " left" : (e.Duration > 0 ? DurText(e.Duration) : ""));
                TxtR(g, right, 20, false, e.ViewCount > 0 && e.Offset == 0 ? Grey : Color.FromArgb(220, 222, 226), rr.Right - 24, y + 21);
                if (e.Offset > 0 && e.Duration > 0)
                {
                    using (SolidBrush b = new SolidBrush(Dim)) g.FillRectangle(b, x + 96, y + rowH - 8, 900, 4);
                    using (SolidBrush b = new SolidBrush(Yellow)) g.FillRectangle(b, x + 96, y + rowH - 8, 900 * Math.Min(1f, (float)e.Offset / e.Duration), 4);
                }
                { int ii = i; Hit(rr, delegate { onSeasons = false; epIdx = ii; }); }
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
            playStarted = DateTime.Now;   // 2.89.0
            lipSync = lip;
            lipDelta = 0;
            lastPos = -1;
            lastDur = 0;
            sentDelay = int.MinValue;
            seekTarget = -1;
            curUrl = file;
            curVer = m != null && m.Versions.Count > 0 ? m.Versions[Math.Max(0, Math.Min(m.Versions.Count - 1, m.VerIdx))] : null;
            Plex.RememberVer(m, curVer);   // 2.92.0: next time it opens on this version
            PublishView();   // 0.9.6: ClipBit decides for each film
            depthOpen = false;   // 2.94.0
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
            frmDone = false; rateUntil = DateTime.MinValue; frmResumeAt = DateTime.MaxValue; frmResumePos = -1; lutCompareOff = false;
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
                    scr = lip || m == null ? Screen2.Settings : Screen2.Details;
                    message = "The player could not open " + (lip ? "the lip sync clip" : (m == null ? "\"" + Path.GetFileName(f) + "\"" : "\"" + m.Title + "\"")) + " (0x" + rc.ToString("X8") + "). The engine's log: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unplugged", "logs", "engine.log");
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
                else if (m != null)
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

        // 2.94.0: Depth while watching: Down opens a bar, Left / Right change it in steps of 5
        // (a paused frame is redrawn at once), Down / Back / Enter close it. 8 s untouched closes it.
        bool depthOpen;
        DateTime depthUntil;
        void DepthOpen()
        {
            if (PHost == null) return;
            depthOpen = true;
            DepthShow();
        }
        void DepthShow()
        {
            depthUntil = DateTime.Now.AddSeconds(8);
            msgUntil = DateTime.MinValue;
            Kick();
        }
        bool DepthKey(Keys k)
        {
            if (k == Keys.Left || k == Keys.Right)
            {
                PHost.SetDepthLevel(PHost.DepthLevel() + (k == Keys.Right ? 5 : -5));
                if (paused) UnpluggedSettings.Redraw();   // a paused frame is only redrawn when asked
                DepthShow();
                return true;
            }
            if (k == Keys.Down || k == Keys.Up || k == Keys.Enter || k == Keys.Escape || k == Keys.Back || k == Keys.BrowserBack)
            {
                depthOpen = false;   // 2.95.1: closes without a message
                Kick();
                return true;
            }
            depthOpen = false;   // any other key closes it and does what it does
            return PlayerKey(k, false, false);
        }

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

        // 0.9.4: while paused the renderer draws only when asked, so ClipGuard's banner and
        // F7 menu are redrawn after their keys, then every second until the menu has had
        // time to close by itself.
#if PLAYER
        DateTime pausedRedrawUntil = DateTime.MinValue;
        public void PausedRedraw(int ms)
        {
            if (playing == null || !paused) return;
            UnpluggedSettings.Redraw();
            DateTime t = DateTime.Now.AddMilliseconds(ms);
            if (t > pausedRedrawUntil) pausedRedrawUntil = t;
        }
#endif

        bool PreviewOn() { return seekTarget >= 0 && DateTime.Now < seekHold; }
        bool BarOn() { return !lipSync && playing != null && (DateTime.Now < barUntil || PreviewOn()); }   // 0.9.4: not held up while paused
        bool OverlayWanted() { return player != null && ((depthOpen && DateTime.Now < depthUntil) || DateTime.Now < msgUntil || DateTime.Now < rateUntil || BarOn() || menuOpen || (capturing && DateTime.Now < capUntil)); }

        long PosMs() { return PreviewOn() ? seekTarget / 10000 : Math.Max(0, lastPos); }
        long DurMs() { return Math.Max(lastDur, playing != null ? playing.Duration : 0); }

        // Keys on the picture. True when the key was used.
        public bool PlayerKey(Keys k, bool shift, bool ctrl)
        {
            if (player == null) return false;   // 2.73.0: Esc no longer closes the player
            if (capturing) return CaptureKey(k, ctrl);
            if (menuOpen) return MenuKey(k);
            if (depthOpen && DateTime.Now > depthUntil) depthOpen = false;   // 2.94.0: closed itself after a while
            if (depthOpen && !ctrl) return DepthKey(k);
            if (paused) ShowBar(3000);   // 0.9.4: any key while paused brings the bar back for 3 s
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
                case Keys.Escape: case Keys.F4: case Keys.Back: case Keys.BrowserBack: case Keys.MediaStop: StopFilm(false); return true;   // 2.73.0: Esc is Back
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
                case Keys.Down: DepthOpen(); return true;   // 2.94.0: the Depth bar
                case Keys.Up: return true;
                case Keys.PageUp: case Keys.MediaPreviousTrack: ChapterJump(-1); return true;
                case Keys.PageDown: case Keys.MediaNextTrack: ChapterJump(1); return true;
                case Keys.Home: SeekTo(0); return true;
                case Keys.I: ToggleBar(); return true;
                case Keys.A: OpenMenu(0); return true;
                case Keys.S: OpenMenu(1); return true;
                case Keys.T: case Keys.Apps: OpenMenu(0); return true;
                case Keys.L:   // 0.9.7: Lens upscale: Lightweight > Standard > Neural 2x first (paused too, for A/B)
                    {
                        int[] order = { 2, 0, 1 };
                        int at = Array.IndexOf(order, LensUp());
                        int nv = order[(at + 1) % 3];
                        SetIni("LensUpscale", nv.ToString(CultureInfo.InvariantCulture));
                        PublishView();
                        PausedRedraw(1500);
                        Say("Lens upscale: " + (nv == 1 ? "Neural 2\u00d7 first" : (nv == 2 ? "Lightweight" : "Standard")), 1500);
                        return true;
                    }
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
            if (cmd == 7) { BannerKey(); return true; }   // 2.83.0: Home (browser home) is F6
            if (capturing)
            {
                // 2.74.0: media buttons sent as WM_APPCOMMAND, in Video Processor.
                Keys t = cmd == 14 ? Keys.MediaPlayPause : cmd == 46 ? Keys.Play : cmd == 47 ? Keys.Pause : cmd == 13 ? Keys.MediaStop
                    : cmd == 11 ? Keys.MediaNextTrack : cmd == 12 ? Keys.MediaPreviousTrack : cmd == 1 ? Keys.BrowserBack : Keys.None;
                if (cmd == 49 || cmd == 50) return PlayerKey(cmd == 49 ? Keys.Right : Keys.Left, true, false);
                return t != Keys.None && CaptureKey(t, false);
            }
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
            ShowBar(3000);   // 0.9.4: paused, the bar goes after 3 s too
        }

        void ToggleBar()
        {
            if (BarOn()) { barUntil = DateTime.MinValue; Kick(); }
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
        // 2.93.1: one press of a remote's Skip button can arrive twice (as a key and as a
        // "next track" command): a second jump the same way within 0.4 s is that, and is
        // dropped. A press soon after a jump counts from where that jump went, not from
        // where the picture still is while the seek lands.
        DateTime chapLast = DateTime.MinValue;
        int chapLastDir;
        long chapLastTarget = -1;
        void ChapterJump(int dir)
        {
            double since = (DateTime.Now - chapLast).TotalMilliseconds;
            if (dir == chapLastDir && since < 400) return;
            chapLast = DateTime.Now; chapLastDir = dir;
            Movie m = playing;
            if (m == null || m.Chapters.Count < 2)
            {
                Engine.Cmd("{\"a\":\"chapter\",\"dir\":" + dir.ToString(CultureInfo.InvariantCulture) + "}");
                Say(dir > 0 ? "Next chapter" : "Chapter back", 1500);
                ShowBar(4000);
                return;
            }
            long pos = PosMs();
            if (chapLastTarget >= 0 && since < 2500) pos = chapLastTarget + 3500;   // the last jump's chapter, as if just past its start
            int ci = ChapterAt(m, pos);
            long target;
            if (dir > 0)
            {
                if (ci + 1 >= m.Chapters.Count) { Say("Last chapter", 1500); return; }
                target = m.Chapters[ci + 1].Start;
            }
            else if (chapLastTarget >= 0 && since < 2500) target = ci > 0 ? m.Chapters[ci - 1].Start : 0;   // pressed again: one more back
            else target = (ci >= 0 && pos - m.Chapters[ci].Start > 3000) ? m.Chapters[ci].Start : (ci > 0 ? m.Chapters[ci - 1].Start : 0);
            chapLastTarget = target;
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
        bool menuOnTabs;   // 2.80.0: the highlight is on the Sound / Subtitles row

        void OpenMenu(int tab)
        {
            RefreshTracks();
            menuOpen = true;
            menuTab = tab;
            menuOnTabs = false;
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

        // 2.80.0: TV-style: Up from the first line goes to the Sound / Subtitles row, where
        // Left / Right picks the tab (Left = Sound, Right = Subtitles, so a doubled key press
        // cannot flip it back); Down or Enter goes back into the list.
        bool MenuKey(Keys k)
        {
            int n = MenuCount();
            if (menuOnTabs)
            {
                switch (k)
                {
                    case Keys.Left: case Keys.A: SetMenuTab(0); break;
                    case Keys.Right: case Keys.S: SetMenuTab(1); break;
                    case Keys.Down: case Keys.Enter: case Keys.Space: menuOnTabs = false; break;
                    case Keys.Escape: case Keys.Back: case Keys.BrowserBack: case Keys.F4: case Keys.T: case Keys.Apps:
                        menuOpen = false; break;
                    default: return true;
                }
                Kick();
                return true;
            }
            switch (k)
            {
                case Keys.Up: if (menuIdx > 0) menuIdx--; else menuOnTabs = true; break;
                case Keys.Down: if (menuIdx < n - 1) menuIdx++; break;
                case Keys.A: SetMenuTab(0); break;
                case Keys.S: SetMenuTab(1); break;
                case Keys.Enter: case Keys.Space: UseTrack(); menuOpen = false; ShowBar(4000); break;
                case Keys.Escape: case Keys.Back: case Keys.BrowserBack: case Keys.F4: case Keys.T: case Keys.Apps:
                    menuOpen = false; break;
                default: return true;   // nothing else while the list is up
            }
            Kick();
            return true;
        }

        void SetMenuTab(int t)
        {
            if (menuTab == t) return;
            menuTab = t;
            menuIdx = menuTab == 0 ? AudioNow() : SubNow() + 1;
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
        // 0.9.4: only the bottom bar is up (the overlay's once-a-second repaint can stay inside it)
        bool OverlayBarOnly()
        {
            DateTime now = DateTime.Now;
            return BarOn() && !menuOpen && now >= msgUntil && now >= rateUntil && !(depthOpen && now < depthUntil)
                && !(capturing && now < capUntil);
        }

        void DrawOverlay(Graphics g, Size size)
        {
            float sc = size.Height / 1080f;
            float scx = sc / LensSqueeze();   // 0.9.5: squeezed for the lens; the bar still spans the width
            float W = size.Width / scx;
            g.ScaleTransform(scx, sc);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            try
            {
                if (BarOn()) DrawBar(g, W);
                if (menuOpen) DrawMenu(g, W);
                if (DateTime.Now < msgUntil) DrawMsg(g, W);
                if (DateTime.Now < rateUntil) DrawRateCard(g, W);   // 0.9.6
                if (depthOpen && DateTime.Now < depthUntil && PHost != null) DrawBeeBar(g, W);   // 2.95.0
                if (capturing && DateTime.Now < capUntil) DrawCapCard(g, W);
            }
            catch (Exception) { }
        }

        static void TxtR(Graphics g, string s, float size, bool bold, Color c, float right, float y)
        {
            Font f = FontC(size, bold);
            using (SolidBrush b = new SolidBrush(c))
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
            if (post != null) DrawFit(g, post, pr);
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
                    DrawFit(g, ct, cr);
                    titleRight = cr.Left - 24;
                }
            }
            // Title: Plex's logo, or the name
            Image logo = logoImg;
            if (logo != null && logo.Height > 0)
            {
                float lh = 66, lw = lh * logo.Width / logo.Height;
                if (lw > Math.Min(560, titleRight - x)) { lw = Math.Min(560, titleRight - x); lh = lw * logo.Height / logo.Width; }
                DrawFit(g, logo, new RectangleF(x, top + 26 + (66 - lh) / 2, lw, lh));
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
                    if (menuOnTabs && i == menuTab) Focus(g, new RectangleF(tx - 14, my + 22, z.Width + 28, 64));   // 2.80.0
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
                bool sel = i == menuIdx && !menuOnTabs;
                if (sel) using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, rr);
                if (i == cur) Txt(g, "\u25CF", 22, false, Yellow, rr.X + 12, ry + 10);
                TextBox(g, name, 26, i == cur, sel ? Color.White : Color.FromArgb(220, 222, 226), new RectangleF(rr.X + 48, ry + 6, rr.Width - 60, row - 10));
                if (sel) Focus(g, rr);
            }
            if (n == 0) Txt(g, "No tracks found yet", 26, false, Grey, mx + 78, my + 116);
            Txt(g, menuOnTabs ? "Left / Right: Sound / Subtitles    Down: the list    Back: close" : "Up / Down: choose    Enter: use    Up at the top: Sound / Subtitles    Back: close", 20, false, Grey, mx + 40, my + mh - 50);
        }

        // 2.95.0: the ClipBee logo: a bee in a yellow honeycomb cell with a white edge (h = its height)
        static readonly Color BeeYellow = Color.FromArgb(255, 198, 26), BeeInk = Color.FromArgb(21, 20, 23);
        internal static void DrawBee(Graphics g, float x, float y, float h)
        {
            float R = h / 2, cx = x + 0.866f * R, cy = y + R;
            Func<float, PointF[]> hex = delegate(float r)
            {
                PointF[] p = new PointF[6];
                for (int i = 0; i < 6; i++) { double a = Math.PI / 180 * (60 * i - 90); p[i] = new PointF(cx + r * (float)Math.Cos(a), cy + r * (float)Math.Sin(a)); }
                return p;
            };
            using (SolidBrush w = new SolidBrush(Color.White)) g.FillPolygon(w, hex(R));
            using (SolidBrush yb = new SolidBrush(BeeYellow)) g.FillPolygon(yb, hex(R * 0.84f));
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                g.FillEllipse(w, cx - R * 0.66f, cy - R * 0.56f, R * 0.56f, R * 0.38f);
                g.FillEllipse(w, cx + R * 0.10f, cy - R * 0.56f, R * 0.56f, R * 0.38f);
            }
            using (SolidBrush k = new SolidBrush(BeeInk))
            {
                g.FillEllipse(k, cx - R * 0.30f, cy - R * 0.22f, R * 0.60f, R * 0.92f);
                g.FillEllipse(k, cx - R * 0.15f, cy - R * 0.50f, R * 0.30f, R * 0.30f);
            }
            using (SolidBrush yb = new SolidBrush(BeeYellow))
            {
                g.FillRectangle(yb, cx - R * 0.34f, cy + R * 0.06f, R * 0.68f, R * 0.11f);
                g.FillRectangle(yb, cx - R * 0.34f, cy + R * 0.30f, R * 0.68f, R * 0.11f);
            }
        }

        // 2.95.0: the ClipBee bar while watching, in the info banner's look: a dark panel along
        // the top with a red line under it, the logo, a red heading, the level in a white-edged
        // badge, a yellow track with 25 marked as recommended, and the keys.
        // 2.95.1: half size, centred at the top (drawn at full size into a half-size transform)
        void DrawBeeBar(Graphics g, float W)
        {
            const float BW = 1880, Scale = 0.5f;
            GraphicsState st = g.Save();
            try
            {
                g.TranslateTransform((W - BW * Scale) / 2, 0);
                g.ScaleTransform(Scale, Scale);
                DrawBeeBarFull(g, BW);
            }
            finally { g.Restore(st); }
        }
        void DrawBeeBarFull(Graphics g, float W)
        {
            int v = PHost.DepthLevel();
            const float H = 150;
            // 1.0.0: the panel and accent follow the theme (the ClipBee yellow stays)
            using (SolidBrush b = new SolidBrush(Bg)) g.FillRectangle(b, 0, 0, W, H);
            using (SolidBrush b = new SolidBrush(Yellow)) g.FillRectangle(b, 0, H - 5, W, 5);
            float x = 56;
            DrawBee(g, x, 30, 86);
            x += 100 + 30;
            using (SolidBrush b = new SolidBrush(Yellow)) g.FillPolygon(b, new[] { new PointF(x, 60), new PointF(x + 14, 70), new PointF(x, 80) });
            Txt(g, "CLIPBEE", 30, true, Yellow, x + 24, 49);
            x += 24 + 170;
            // the level, in a badge like the banner's
            string vt = v == 0 ? "Off" : v.ToString(CultureInfo.InvariantCulture);
            RectangleF bd = new RectangleF(x, 34, 130, 76);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(74, 79, 92))) g.FillRectangle(b, bd);
            using (Pen p = new Pen(Color.White, 4)) g.DrawRectangle(p, bd.X, bd.Y, bd.Width, bd.Height);
            using (Font f = F(44, true)) using (SolidBrush b = new SolidBrush(Color.White)) using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(vt, f, b, bd, sf);
            x += 130 + 50;
            // the track
            float tw = Math.Max(300, W - x - 560), ty = 62, th = 20;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(45, 46, 53))) g.FillRectangle(b, x, ty, tw, th);
            using (SolidBrush b = new SolidBrush(BeeYellow)) g.FillRectangle(b, x, ty, tw * v / 100f, th);
            using (Pen p = new Pen(Color.White, 3)) g.DrawRectangle(p, x, ty, tw, th);
            float kx = x + tw * v / 100f;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - 18, ty + th / 2 - 18, 36, 36);
            using (SolidBrush b = new SolidBrush(BeeInk)) g.FillEllipse(b, kx - 11, ty + th / 2 - 11, 22, 22);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - 8, ty + th / 2 - 8, 16, 16);
            // marks under it: Off, 25 recommended, 50, 100
            float rx = x + tw * 0.25f;
            using (SolidBrush b = new SolidBrush(BeeYellow)) g.FillPolygon(b, new[] { new PointF(rx, ty + th + 6), new PointF(rx - 9, ty + th + 18), new PointF(rx + 9, ty + th + 18) });
            Txt(g, "Off", 20, false, Grey, x - 4, ty + th + 14);
            Txt(g, "25 recommended", 20, true, BeeYellow, rx - 70, ty + th + 20);
            Txt(g, "50", 20, false, Grey, x + tw * 0.5f - 12, ty + th + 14);
            Txt(g, "100", 20, false, Grey, x + tw - 34, ty + th + 14);
            x += tw + 50;
            Txt(g, "\u25c0 \u25b6  adjust", 28, true, Color.White, x, 52);
            Txt(g, "Down closes", 28, true, Color.White, x + 230, 52);
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
                tick.Tick += delegate { Tick(); };
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
            // 0.9.4: the 250 ms tick only keeps it shown or hidden; it repaints once a second
            // (the clock and position), and only the bottom bar's strip when that is all that
            // shows. Keys (Kick) still repaint at once. Before, the whole 4K window was cleared
            // and drawn four times a second.
            int tickN;
            void Tick()
            {
                Form v = owner.video;
                if (v == null || !v.Visible || !owner.OverlayWanted()) { if (Visible) Hide(); return; }
                if (!Visible || Bounds != v.Bounds) { Kick(); return; }
                if (++tickN % 4 != 0) return;
                if (owner.OverlayBarOnly() && owner.GeoPts() == null)   // 2.108.0: bent, it repaints whole
                {
                    float sc = ClientSize.Height / 1080f;
                    int top = (int)Math.Floor((1080 - 252) * sc);
                    Invalidate(new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top));
                }
                else Invalidate();
            }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                int[] gp = owner.GeoPts();   // 2.108.0: bent like the film under it
                if (gp != null && ClientSize.Width > 16 && ClientSize.Height > 16)
                {
                    try
                    {
                        using (Bitmap flat = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (Graphics fg = Graphics.FromImage(flat)) { fg.Clear(KeyColor); owner.DrawOverlay(fg, ClientSize); }
                            using (Bitmap bent = GeoBend(flat, gp, KeyColor.ToArgb())) e.Graphics.DrawImageUnscaled(bent, 0, 0);
                        }
                        return;
                    }
                    catch (Exception) { }
                }
                e.Graphics.Clear(KeyColor);   // honours the clip: only the area being repainted
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
                public bool FromLut;   // 2.91.0: the value comes from the LUT's measured levels
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
            new R(4,2,"out.lutLevels",0.0,0.0,0.0,false,0,2,null,1),
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
#if PLAYER
                new PRow(S_ENH, "!clipbit", "ClipBit", null, 0, "", "Brings back fine texture lost to a low bitrate: Detail raises the finest texture (no halos, flat areas left alone) and Grain adds fine film grain. 4K films only. On by itself for films under the limit; Always for every 4K film; Off never. The banner (F6) shows whether it is on."),
                new PRow(S_ENH, "!cbdetail", "ClipBit detail", null, 0, "", "How much finest texture ClipBit brings back (0-100, 80 recommended)."),
                new PRow(S_ENH, "!cbgrain", "ClipBit grain", null, 0, "", "How much fine grain ClipBit adds (0-100, 50 recommended). Black and highlights stay clean."),
#endif
                new PRow(S_ENH, "!depth", "ClipBee", null, 0, "", "Adds snap and depth: texture, outlines and the separation of foreground from background, on brightness only (0-100, 0 is off, 25 recommended). Grain in flat areas, near-black and highlights are left alone. While watching: Down opens it, Left / Right change it. With ClipBee on, Unplugged's micro contrast is turned off."),
                new PRow(S_ENH, "postfx.sharpen", "Adaptive sharpen", null, 0, "", "Adaptive sharpening on or off."),
                new PRow(S_ENH, "postfx.sharpenStr", "Sharpen strength", null, 0.01, "", "How strong the sharpening is."),
                new PRow(S_ENH, "postfx.deband", "Deband", null, 0, "", "Smooths banding in gradients."),
                new PRow(S_ENH, "postfx.debandThresh", "Deband threshold", null, 1, "", "Deband: threshold."),
                new PRow(S_ENH, "postfx.debandRange", "Deband range", null, 1, "", "Deband: range."),
                new PRow(S_ENH, "postfx.debandIter", "Deband iterations", null, 1, "", "Deband: iterations."),
                new PRow(S_ENH, "postfx.debandGrain", "Deband grain", null, 1, "", "Deband: grain."),
                new PRow(S_SCALE, "out.upscaling", "Upscaling", new[] { "Lanczos3", "Jinc2", "FSRCNNX" }, 0, "", "FSRCNNX is ClipGuard's neural upscaler for about 2x (1080p on 4K); other sizes use Jinc2. Also in F7."),
                new PRow(S_SCALE, "out.downscaling", "Downscaling", new[] { "Lanczos", "BicubicSharp", "SSimDownscaler" }, 0, "", "How the picture is downscaled. SSimDownscaler keeps the source's fine detail and contrast that a plain downscale softens."),
                new PRow(S_SCALE, "!ravu", "RAVU-Zoom-AR", null, 0, "", "Upscales pictures that are not about 2x (where FSRCNNX runs), the lens's taller-only stretch included: sharper edges than Jinc2 or Lanczos, with no ringing. Off: the Upscaling setting."),
                new PRow(S_SCALE, "out.chromaScaling", "Chroma upsampling", new[] { "CatmullRom", "KrigBilateral", "JointBilateral", "CfL" }, 0, "", "How the colour (chroma) is upscaled to the picture's full size. JointBilateral and CfL use the full-size brightness picture as a guide, for sharper colour edges with no ringing; CfL (chroma from luma) is the sharpest."),
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
#if PLAYER
                new PRow(S_GEO, "!lens", "Anamorphic lens", null, 0, "", "The lens's factor, or Off. With a lens, a scope film (2.2 or wider) fills the screen with its letterbox cut off; any other film fills the screen's height at its own shape (a 2.00 or 2.10 letterbox is cut off too) with black at the sides, squeezed for the lens. Decided once per film. Leave Fit mode on Source."),
                new PRow(S_GEO, "!scope", "Scope screen", null, 0, "", "The screen's shape: 2.35 or 2.40."),
                new PRow(S_GEO, "!lensup", "Lens upscale", null, 0, "", "How a scope film is stretched to fill the screen's height for the lens. Lightweight: Lanczos3 (easiest on the GPU). Standard: RAVU-Zoom-AR (the Upscaling setting with RAVU off). Neural 2x first: the letterbox is cut off, the picture is doubled by FSRCNNX, then brought down to the exact size by SSimDownscaler. If frames keep being skipped, it switches itself off for that film. 4K films too. A powerful GPU is required; watch the dropped frames on the banner (F6)."),
                new PRow(S_GEO, "!shape", "Film shape", null, 0, "", "Auto: read from the picture at the film's start (a scope letterbox in the first few seconds of picture makes it scope for the whole film; it never switches back). Scope or Not scope: always that."),
                new PRow(S_GEO, "!geodef", "Define screen...", null, 0, "", "12-point screen geometry, all on the edges: 4 along the top, 4 along the bottom, 2 on each side. Arrows move the point, OK goes to the next one round the edge, Back saves, Stop undoes. Saved and loaded at every start."),
                new PRow(S_GEO, "!geo", "Screen geometry", null, 0, "", "The defined screen geometry on or off."),
                new PRow(S_GEO, "!georeset", "Reset geometry", null, 0, "", "Puts all 12 points back where they started."),
#endif
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
                new PRow(S_LUT, "!calrun", "Calibrate a projector...", null, 0, "", "Measures the projector with the meter and builds a 3D LUT around your white, with a check and a PDF report. Enter starts: the projector's name, then a .ccss correction from the Calibration folder, or none."),
                new PRow(S_LUT, "!lut", "LUT", null, 0, "", "The calibration LUT the picture uses: None, or a projector you calibrated. F5 while watching turns it off and on for comparing."),
                new PRow(S_LUT, "out.cubeLutMode", "LUT mode", new[] { "Off", "Gamma only", "Greyscale", "Full 3D" }, 0, "", "How the calibration LUT is applied (Full 3D for a calibration)."),
                new PRow(S_LUT, "out.lutLevels", "Display levels", new[] { "From LUT", "Manual" }, 0, "", "From LUT: SDR display nits, Static nits and Display black are the white and black the calibration measured. Manual: your own values. Changing one of those rows switches to Manual."),
                new PRow(S_LUT, "!caldir", "Calibration folder", null, 0, "", "Put .ccss corrections here (any name; the calibration lets you pick one). The LUTs and reports are saved here too."),
                new PRow(S_OUT, "!gamma", "Output gamma", null, 0, "", "Set it to your projector's gamma. 2.2 is Unplugged's own; 2.4 and BT.1886 re-encode the picture at the very end. With a calibration LUT on, the LUT sets the gamma and this is not used."),
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
                if (r.Key == "out.upscaling" && GetFsr != null && GetFsr() > 0) return GetFsr() == 2 ? "ArtCNN C4F32" : "FSRCNNX";
                if (r.Key == "out.chromaScaling" && GetCg != null && GetCg() > 0) return GetCg() == 2 ? "CfL" : "JointBilateral";   // 2.111.0
                if (r.Key == "out.downscaling" && GetSsim != null && GetSsim() > 0) return "SSimDownscaler";   // 2.114.0
                if (r.Kind == 2) return p.Names != null && r.Opt >= 0 && r.Opt < p.Names.Length ? p.Names[r.Opt] : r.Opt.ToString(CultureInfo.InvariantCulture);
                return r.Val.ToString(p.Step < 0.01 ? "0.000" : (p.Step < 0.1 ? "0.00" : (p.Step < 1 ? "0.0" : "0")), CultureInfo.InvariantCulture) + p.Unit + (r.FromLut ? " (LUT)" : "");
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

            // 2.108.0: Output gamut is BT.2020 (the colorimetry flag follows it)
            public static bool Output2020()
            {
                try { R r = Find(Load(), "hdr.outputGamut"); return r != null && r.Opt == 1; }
                catch (Exception) { return false; }
            }

            public static Func<int> GetFsr;      // 2.85.0: 0 none, 1 FSRCNNX, 2 ArtCNN C4F32
            public static Func<int> GetCg;       // 2.111.0: chroma: 0 Unplugged's, 1 JointBilateral, 2 CfL
            public static Action<int> SetCg;
            public static Func<int> GetSsim;     // 2.114.0: downscaling: 1 SSimDownscaler (over Unplugged's choice)
            public static Action<int> SetSsim;
            public static Action<int> SetFsr;

            public static void PictureStep(int i, int dir)
            {
                if (i < 0 || i >= PRows.Length) return;
                PRow p = PRows[i];
                if (p.Key.StartsWith("!")) return;
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return; }
                R r = Find(rows, p.Key);
                if (r == null || r.Kind == 4) return;
                if (r.FromLut) SetValue("out.lutLevels", "1");   // 2.91.0: a hand change wins over the LUT's levels
                if (r.Kind == 0) SetValue(r.Key, r.On ? "false" : "true");
                else if (r.Kind == 1)
                {
                    double v = Math.Max(r.Mn, Math.Min(r.Mx, Math.Round((r.Val + dir * p.Step) / p.Step) * p.Step));
                    SetValue(r.Key, v.ToString("G6", CultureInfo.InvariantCulture));
                }
                else if (r.Key == "out.upscaling" && SetFsr != null)
                {
                    // Lanczos3, Jinc2, FSRCNNX, ArtCNN C4F32 (the neural ones with Jinc2 for other sizes).
                    int nn = GetFsr != null ? GetFsr() : 0;
                    int cur = nn > 0 ? 1 + nn : r.Opt;
                    int o = ((cur + dir) % 4 + 4) % 4;
                    SetValue(r.Key, (o == 0 ? r.OptVals[0] : r.OptVals[1]).ToString(CultureInfo.InvariantCulture));
                    SetFsr(o >= 2 ? o - 1 : 0);
                }
                else if (r.Key == "out.downscaling" && SetSsim != null)
                {
                    // 2.114.0: Lanczos, BicubicSharp, SSimDownscaler (through ClipGuard; Unplugged's
                    // choice stays set underneath it).
                    int cur = (GetSsim != null && GetSsim() > 0) ? 2 : r.Opt;
                    int o = ((cur + dir) % 3 + 3) % 3;
                    if (o < 2) SetValue(r.Key, r.OptVals[o].ToString(CultureInfo.InvariantCulture));
                    SetSsim(o == 2 ? 1 : 0);
                }
                else if (r.Key == "out.chromaScaling" && SetCg != null)
                {
                    // 2.111.0: CatmullRom, KrigBilateral, JointBilateral, CfL. The last two go to the
                    // renderer through ClipGuard (Unplugged knows only the first two; KrigBilateral
                    // stays set underneath them).
                    int cg = GetCg != null ? GetCg() : 0;
                    int cur = cg > 0 ? 1 + cg : r.Opt;
                    int o = ((cur + dir) % 4 + 4) % 4;
                    SetValue(r.Key, (o == 0 ? r.OptVals[0] : r.OptVals[1]).ToString(CultureInfo.InvariantCulture));
                    SetCg(o >= 2 ? o - 1 : 0);
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
                ApplyLutLevels(rows);
                return rows;
            }

            // 2.91.0: a calibration LUT's .txt carries the white and black it measured
            // (white_nits= / black_nits=). With Display levels on From LUT and a LUT in use,
            // they replace SDR display nits, Static nits and Display black.
            static readonly string[] LevelRows = { "hdr.sdrNits", "hdr.maxNits", "hdr.blackNits" };
            public static bool LutLevels(string cube, out double white, out double black)
            {
                white = 0; black = -1;
                try
                {
                    if (string.IsNullOrEmpty(cube)) return false;
                    string t = Path.ChangeExtension(cube, ".txt");
                    if (!File.Exists(t)) return false;
                    foreach (string raw in File.ReadAllLines(t))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                        if (k == "white_nits") white = Num(v, 0);
                        else if (k == "black_nits") black = Num(v, -1);
                    }
                }
                catch (Exception) { return false; }
                return white >= 10 && black >= 0;
            }
            static void ApplyLutLevels(List<R> rows)
            {
                R lv = Find(rows, "out.lutLevels"), lp = Find(rows, "out.cubeLut"), lm = Find(rows, "out.cubeLutMode");
                if (lv == null || lp == null || lm == null || lv.Opt != 0 || lm.Opt == 0) return;
                double w, b;
                if (!LutLevels(lp.File, out w, out b)) return;
                foreach (R r in rows)
                {
                    if (Array.IndexOf(LevelRows, r.Key) < 0) continue;
                    double v = r.Key == "hdr.blackNits" ? Math.Round(b, 3) : Math.Round(w);
                    r.Val = Math.Max(r.Mn, Math.Min(r.Mx, v));
                    r.FromLut = true;
                }
            }
            static string LevelSets(List<R> rows)
            {
                StringBuilder sb = new StringBuilder();
                foreach (string k in LevelRows)
                {
                    R r = Find(rows, k);
                    if (r == null) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(EngineKey(k)).Append('=').Append(r.Val.ToString("G6", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
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
                string lv = LevelSets(rows);   // 2.91.0: the display levels follow the LUT
                if (lv.Length > 0) Engine.Cmd("{\"a\":\"set\",\"sets\":\"" + lv + "\"}");
            }

            // 2.93.2: a paused frame is only redrawn when the engine is sent settings: the
            // display levels are sent again as they are, which asks for that redraw.
            public static void Redraw()
            {
                List<R> rows;
                try { rows = Load(); } catch (Exception) { return; }
                string lv = LevelSets(rows);
                if (lv.Length > 0) Engine.Cmd("{\"a\":\"set\",\"sets\":\"" + lv + "\"}");
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
            bool fel = false;
            player = null;
            playing = null;
            lipSync = false;
            if (!Visible) { Show(); Activate(); }
            if (lip || fel)
            {
                scr = Screen2.Settings; tab = TabSettings; navFocus = false;
#if PLAYER
                setIdx = fel ? 12 : 1;   // Lip sync test (2.71.0: one row higher); 2.87.1: FEL test clips
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
            if (mouseTimer != null) mouseTimer.Stop();
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

        // ---- 0.9.2: mouse. Whatever is drawn as a choice registers where it is (Hit); the
        // pointer selects it, a click selects and opens it (OK), right-click is Back, the wheel
        // is Up / Down. Dialogs and menus still take their keys (a click there is OK).
        sealed class HitZone { public RectangleF R; public Action Select; }
        readonly List<HitZone> hits = new List<HitZone>();
        Point lastMouse = new Point(-1, -1);
        DateTime mouseQuietUntil = DateTime.MinValue;

        void Hit(RectangleF r, Action select)
        {
            if (DialogOpen()) return;   // a dialog over the page has the mouse
            hits.Add(new HitZone { R = r, Select = select });
        }

        bool DialogOpen()
        {
#if PLAYER
            if (calState != 0 || aboutOn || geoEdit) return true;
#endif
            return learning;
        }

        // 0.9.5: with an anamorphic lens the player's own screens are drawn squeezed sideways
        // by its factor (centred), so through the lens they come out the right shape.
        float LensSqueeze()
        {
#if PLAYER
            return scrProf.Lens > 100 ? scrProf.Lens / 100f : 1f;
#else
            return 1f;
#endif
        }

        PointF ToLayout(Point p)
        {
            float sx = ClientSize.Width / 1920f, sy = ClientSize.Height / 1080f, sc = Math.Min(sx, sy);
            float scx = sc / LensSqueeze();
            return new PointF((p.X - (ClientSize.Width - 1920 * scx) / 2) / scx, (p.Y - (ClientSize.Height - 1080 * sc) / 2) / sc);
        }

        HitZone HitAt(Point p)
        {
            PointF q = ToLayout(p);
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(q)) return hits[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Math.Abs(e.X - lastMouse.X) + Math.Abs(e.Y - lastMouse.Y) < 3) return;   // only a real move selects
            lastMouse = e.Location;
            if (DateTime.Now < mouseQuietUntil) return;
            HitZone z = HitAt(e.Location);
            if (z != null) { z.Select(); Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Right) { OnKey(Keys.Escape); Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (DialogOpen() || sortOpen) { OnKey(Keys.Enter); Invalidate(); return; }
            HitZone z = HitAt(e.Location);
            if (z == null) return;
            z.Select();
            // a settings row with a value: a click steps it, like Right; everything else opens
            Keys k = Keys.Enter;
            if (scr == Screen2.Settings && !navFocus)
            {
                string[] rows = SettingsRows();
                if (setIdx >= 0 && setIdx < rows.Length && rows[setIdx].Contains("Left / Right")) k = Keys.Right;
            }
            mouseQuietUntil = DateTime.Now.AddMilliseconds(400);   // what opens can sit under the pointer
            OnKey(k);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int n = Math.Max(1, Math.Abs(e.Delta) / 120);
            Keys k = e.Delta > 0 ? Keys.Up : Keys.Down;
            // a row of posters scrolls sideways
            if ((scr == Screen2.Home) && !navFocus && (ModifierKeys & Keys.Shift) != 0) k = e.Delta > 0 ? Keys.Left : Keys.Right;
            for (int i = 0; i < n; i++) OnKey(k);
            mouseQuietUntil = DateTime.Now.AddMilliseconds(300);
            Invalidate();
        }

        // The pointer shows while the mouse moves and hides after 3 s still. During a film
        // (whose picture belongs to the engine's window) the mouse is read here: a move shows
        // the playback bar, a left click is Play / Pause, a right click is Back.
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        System.Windows.Forms.Timer mouseTimer;
        Point mousePos = new Point(int.MinValue, 0);
        DateTime mouseMoved = DateTime.MinValue;
        bool mouseL, mouseR;

        void StartMouseTimer()
        {
            if (mouseTimer != null) return;
            mouseTimer = new System.Windows.Forms.Timer();
            mouseTimer.Interval = 60;
            mouseTimer.Tick += delegate { MouseTick(); };
            mouseTimer.Start();
        }

        static bool OursInFront()
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch (Exception) { return false; }
        }

        void MouseTick()
        {
            if (closing) return;
            bool front = OursInFront();
            Point p = Cursor.Position;
            bool moved = mousePos.X != int.MinValue && (Math.Abs(p.X - mousePos.X) + Math.Abs(p.Y - mousePos.Y) >= 3);
            mousePos = p;
            if (moved && front)
            {
                mouseMoved = DateTime.Now;
                if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
#if PLAYER
                if (playing != null && player != null && !capturing) ShowBar(3000);
#endif
            }
            else if (!cursorHidden && (DateTime.Now - mouseMoved).TotalSeconds > 3) { Cursor.Hide(); cursorHidden = true; }
#if PLAYER
            bool l = (GetAsyncKeyState(0x01) & 0x8000) != 0, r = (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (front && playing != null && player != null && !capturing)
            {
                if (l && !mouseL) PlayerKey(menuOpen ? Keys.Enter : Keys.MediaPlayPause, false, false);
                if (r && !mouseR) PlayerKey(Keys.Escape, false, false);
            }
            mouseL = l; mouseR = r;
#endif
        }

        // ---- drawing (a 1920 x 1080 layout, scaled to the screen) -------------------------------
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
#if PLAYER
            // 2.108.0: with screen geometry on, drawn whole off screen, then bent to the screen
            int[] gp = GeoPts();
            if (gp != null && ClientSize.Width > 16 && ClientSize.Height > 16)
            {
                try
                {
                    using (Bitmap flat = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics fg = Graphics.FromImage(flat)) PaintFlat(fg);
                        using (Bitmap bent = GeoBend(flat, gp, 0)) e.Graphics.DrawImageUnscaled(bent, 0, 0);
                    }
                    return;
                }
                catch (Exception) { }
            }
#endif
            PaintFlat(e.Graphics);
        }
        void PaintFlat(Graphics g)
        {
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float sx = ClientSize.Width / 1920f, sy = ClientSize.Height / 1080f, sc = Math.Min(sx, sy);
            float scx = sc / LensSqueeze();   // 0.9.5
            g.TranslateTransform((ClientSize.Width - 1920 * scx) / 2, (ClientSize.Height - 1080 * sc) / 2);
            g.ScaleTransform(scx, sc);
            hits.Clear();   // 0.9.2: the mouse targets, as drawn
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
                        else if (scr == Screen2.Power) DrawPower(g);
#endif
                        else DrawSettings(g);
                        break;
                }
                if (sortOpen && (scr == Screen2.Movies || scr == Screen2.Shows)) DrawSortMenu(g);   // 2.86.1
                if (message.Length > 0) DrawMessage(g);
                if (learning) DrawLearn(g);
#if PLAYER
                if (calState != 0) DrawCalDlg(g);
                if (aboutOn) DrawAbout(g);   // 2.89.1
                if (geoEdit) DrawGeoEdit(g);   // 0.9.5
#endif
            }
            catch (Exception) { }
        }

        // ---- 2.104.0: speed: images and fonts ------------------------------------------
        // Every image is kept a second time at the exact size it is drawn on the screen
        // (4K pixels), made once with high-quality scaling, then drawn 1:1. A backdrop's dim
        // layer is baked into its copy. Before, every redraw rescaled every poster and the
        // 4K backdrop with the slowest scaling GDI+ has, on each remote key.
        sealed class ScaledKey : IEquatable<ScaledKey>
        {
            public Image Src; public int W, H, Dim; public Color DimC;
            public bool Equals(ScaledKey o) { return o != null && ReferenceEquals(Src, o.Src) && W == o.W && H == o.H && Dim == o.Dim && DimC.ToArgb() == o.DimC.ToArgb(); }
            public override bool Equals(object o) { return Equals(o as ScaledKey); }
            public override int GetHashCode() { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Src) ^ (W * 397) ^ (H * 7919) ^ (Dim << 24) ^ DimC.ToArgb(); }
        }
        static readonly Dictionary<ScaledKey, LinkedListNode<KeyValuePair<ScaledKey, Bitmap>>> scaled = new Dictionary<ScaledKey, LinkedListNode<KeyValuePair<ScaledKey, Bitmap>>>();
        static readonly LinkedList<KeyValuePair<ScaledKey, Bitmap>> scaledLru = new LinkedList<KeyValuePair<ScaledKey, Bitmap>>();
        static long scaledBytes;
        const long ScaledMax = 320L << 20;   // about 320 MB of ready-sized images

        static Bitmap Scaled(Image src, int w, int h, int dim, Color dimC)
        {
            ScaledKey k = new ScaledKey { Src = src, W = w, H = h, Dim = dim, DimC = dimC };
            LinkedListNode<KeyValuePair<ScaledKey, Bitmap>> node;
            if (scaled.TryGetValue(k, out node)) { scaledLru.Remove(node); scaledLru.AddFirst(node); return node.Value.Value; }
            Bitmap b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics bg = Graphics.FromImage(b))
            {
                bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                bg.CompositingQuality = CompositingQuality.HighQuality;
                using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);   // clean edges
                    bg.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
                if (dim > 0) using (SolidBrush db = new SolidBrush(Color.FromArgb(dim, dimC))) bg.FillRectangle(db, 0, 0, w, h);
            }
            node = scaledLru.AddFirst(new KeyValuePair<ScaledKey, Bitmap>(k, b));
            scaled[k] = node;
            scaledBytes += (long)w * h * 4;
            while (scaledBytes > ScaledMax && scaledLru.Count > 1)
            {
                LinkedListNode<KeyValuePair<ScaledKey, Bitmap>> last = scaledLru.Last;
                scaledLru.RemoveLast();
                scaled.Remove(last.Value.Key);
                scaledBytes -= (long)last.Value.Value.Width * last.Value.Value.Height * 4;
                last.Value.Value.Dispose();
            }
            return b;
        }

        // Draws src into r (screen units) through its ready-sized copy. dim: a layer of dimC
        // at that alpha baked on top (0 = none).
        static void DrawFit(Graphics g, Image src, RectangleF r, int dim = 0, Color dimC = default(Color))
        {
            if (src == null || r.Width <= 0 || r.Height <= 0) return;
            float s, sy;
            using (Matrix mt = g.Transform) { s = Math.Abs(mt.Elements[0]); sy = Math.Abs(mt.Elements[3]); }
            if (s <= 0) s = 1;
            if (sy <= 0) sy = s;
            int w = Math.Max(1, (int)Math.Round(r.Width * s)), h = Math.Max(1, (int)Math.Round(r.Height * sy));   // 0.9.5: x and y apart (the lens squeeze)
            Bitmap b;
            try { b = Scaled(src, w, h, dim, dimC); }
            catch (Exception) { g.DrawImage(src, r); return; }   // e.g. out of memory: draw it the old way
            InterpolationMode im = g.InterpolationMode;
            PixelOffsetMode po = g.PixelOffsetMode;
            CompositingQuality cq = g.CompositingQuality;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 1:1: nothing to scale
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.DrawImage(b, r);
            g.InterpolationMode = im; g.PixelOffsetMode = po; g.CompositingQuality = cq;
        }

        // Fonts made once per size and kept (Txt, TextBox and the like made a new one for
        // every line of text on every redraw).
        static readonly Dictionary<long, Font> fontCache = new Dictionary<long, Font>();
        static Font FontC(float size, bool bold)
        {
            long k = ((long)Math.Round(size * 100) << 1) | (bold ? 1L : 0L);
            Font f;
            if (!fontCache.TryGetValue(k, out f)) { f = F(size, bold); fontCache[k] = f; }
            return f;
        }
        static readonly Dictionary<long, Font> fontCacheC = new Dictionary<long, Font>();

        static Font F(float size, bool bold) { return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }

        static void Txt(Graphics g, string s, float size, bool bold, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, FontC(size, bold), b, x, y);
        }

        static void TextBox(Graphics g, string s, float size, bool bold, Color c, RectangleF r)
        {
            Font f = FontC(size, bold);
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisWord;
                g.DrawString(s, f, b, r, sf);
            }
        }

        static void Center(Graphics g, string s, float size, Color c, float y)
        {
            Font f = FontC(size, false);
            using (SolidBrush b = new SolidBrush(c))
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
                    { int ii = i; Hit(new RectangleF(x - 4, 44, sz.Width + 8, 40), delegate { navFocus = true; if (tab != ii) { tab = ii; scr = TabScreen(tab); } }); }
                    if (navFocus && i == tab) Focus(g, new RectangleF(x - 4, 44, sz.Width + 8, 40));
                    x += sz.Width + 40;
                }
            Txt(g, "Arrows: move    Enter: open    Esc: back    F4: Video Processor", 18, false, Grey, 96, 1036);
        }

        void Poster(Graphics g, Movie m, int idx, float x, float y, float w, float h, bool sel)
        {
            RectangleF r = new RectangleF(x, y, w, h);
            Image im = Img(m, m.Poster, w > 300 ? 720 : 440, w > 300 ? 1080 : 660);   // 2.52.1: sharp on a 4K screen; 2.68.0: an episode shows its show's poster
            if (im != null) DrawFit(g, im, r);
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
            if (w < 330) { float yb = ResBadge(g, m.Res, x + w - 7, y + 7); HdrTag(g, m, x + w - 7, yb + 4); }   // 2.88.0; 2.89.0: and the HDR tag
            if (sel) Focus(g, r);
        }

        // 2.88.0: the film's resolution on its cover: a small chrome-rimmed plate in the top
        // right, silver lettering over a light bar in the resolution's colour (4K red, 1080p
        // blue, 720p and below yellow).
        static readonly string BadgeFamily = PickBadgeFamily();
        static string PickBadgeFamily()
        {
            try
            {
                foreach (FontFamily f in FontFamily.Families)
                    if (f.Name == "Bahnschrift SemiBold Condensed") return f.Name;
                foreach (FontFamily f in FontFamily.Families)
                    if (f.Name == "Bahnschrift") return f.Name;
            }
            catch (Exception) { }
            return "Segoe UI";
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- 2.89.0: HDR tags on the covers. Plex's library lists have no stream details, so
        // the HDR type (and Dolby Vision profile) of films without them is fetched in the
        // background, 40 at a time (/library/metadata/<k1>,<k2>,...), and kept in the cache.
        // Whether a Dolby Vision enhancement layer is full or minimal is not in Plex: the
        // renderer reads it from the RPU while the film plays, and it is remembered here.
        volatile bool hdrFilling;
        // 2.92.0: the home rows keep their own copies of a film; each copy gets the HDR
        // details another copy of it already has, so Continue Watching and Recently Added
        // show the tags too.
        void ShareMedia()
        {
            Dictionary<string, Movie> have = new Dictionary<string, Movie>();
            List<Movie>[] lists = { movies, recent, cont };
            foreach (List<Movie> l in lists)
                if (l != null) foreach (Movie m in l)
                    if (m != null && m.Srv != null && m.Hdr.Length > 0 && !have.ContainsKey(m.Srv.Id + ":" + m.Key)) have[m.Srv.Id + ":" + m.Key] = m;
            foreach (List<Movie> l in lists)
                if (l != null) foreach (Movie m in l)
                {
                    Movie h;
                    if (m == null || m.Srv == null || m.Hdr.Length > 0 || !have.TryGetValue(m.Srv.Id + ":" + m.Key, out h)) continue;
                    m.Hdr = h.Hdr; m.Dovi = h.Dovi;
                    if (m.Res.Length == 0) m.Res = h.Res;
                    if (m.Audio.Length == 0) m.Audio = h.Audio;
                }
        }

        void HdrFill()
        {
            ShareMedia();
            if (hdrFilling) return;
            List<Movie> want = new List<Movie>();
            HashSet<string> seen = new HashSet<string>();
            foreach (List<Movie> l in new[] { movies, recent, cont })
                foreach (Movie m in l)
                    if (m != null && m.Srv != null && m.Hdr.Length == 0 && m.Type != "show" && seen.Add(m.Srv.Id + ":" + m.Key)) want.Add(m);
            if (want.Count == 0) return;
            hdrFilling = true;
            Bg2(delegate
            {
                Dictionary<string, List<Movie>> bySrv = new Dictionary<string, List<Movie>>();
                foreach (Movie m in want) { if (!bySrv.ContainsKey(m.Srv.Id)) bySrv[m.Srv.Id] = new List<Movie>(); bySrv[m.Srv.Id].Add(m); }
                int batchGot = 0, singleGot = 0, singleTried = 0, failed = 0;
                foreach (List<Movie> l in bySrv.Values)
                    for (int i = 0; i < l.Count; i += 40)
                    {
                        List<Movie> part = l.GetRange(i, Math.Min(40, l.Count - i));
                        Dictionary<string, Movie> byKey = new Dictionary<string, Movie>();
                        foreach (Movie m in part) byKey[m.Key] = m;
                        try
                        {
                            object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(part[0].Srv, "/library/metadata/" + string.Join(",", new List<string>(byKey.Keys).ToArray())), part[0].Srv.Token, 30000)), "MediaContainer");
                            foreach (object md in Plex.Arr(mc, "Metadata"))
                            {
                                Movie m;
                                if (!byKey.TryGetValue(Plex.Str(md, "ratingKey"), out m)) continue;
                                bool was = m.Detailed;
                                Plex.FillMedia(m, md);
                                m.Detailed = was;   // only the media: opening the film still fetches the rest
                                if (m.Hdr.Length > 0) batchGot++;
                            }
                        }
                        catch (Exception) { failed++; }
                    }
                // 2.89.1: a request for many films can come back without their streams; the
                // rest one at a time, as the details screen asks (a short pause between them)
                foreach (Movie m in want)
                {
                    if (m.Hdr.Length > 0) continue;
                    singleTried++;
                    try
                    {
                        object mc = Plex.Get(Plex.Json(Plex.Http("GET", Url(m.Srv, "/library/metadata/" + m.Key), m.Srv.Token, 15000)), "MediaContainer");
                        object[] md = Plex.Arr(mc, "Metadata");
                        if (md.Length > 0)
                        {
                            bool was = m.Detailed;
                            Plex.FillMedia(m, md[0]);
                            m.Detailed = was;
                            if (m.Hdr.Length > 0) singleGot++;
                        }
                    }
                    catch (Exception) { failed++; }
                    Thread.Sleep(30);
                }
                try
                {
                    File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "MediaPlayer", "hdrfill.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  wanted " + want.Count + ", from batches " + batchGot
                        + ", one by one " + singleGot + " of " + singleTried + ", failed requests " + failed + "\r\n");
                }
                catch (Exception) { }
            }, delegate { hdrFilling = false; ShareMedia(); SaveLibraryCache(); Invalidate(); }, delegate(Exception ex) { hdrFilling = false; });
        }

        // Full / minimal enhancement layers seen while playing, by server and film.
        static Dictionary<string, string> dvElSeen;
        static string DvElPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "MediaPlayer", "dvel.txt"); } }
        static string DvElOf(Movie m)
        {
            if (m.DvEl.Length > 0 || m.Srv == null) return m.DvEl;
            if (dvElSeen == null)
            {
                dvElSeen = new Dictionary<string, string>();
                try { foreach (string l in File.ReadAllLines(DvElPath)) { string[] f = l.Split('\t'); if (f.Length == 2) dvElSeen[f[0]] = f[1]; } } catch (Exception) { }
            }
            string v;
            if (dvElSeen.TryGetValue(m.Srv.Id + ":" + m.Key, out v)) m.DvEl = v;
            return m.DvEl;
        }
        static void DvElRemember(Movie m, string v)
        {
            if (m == null || m.Srv == null || DvElOf(m) == v) return;
            m.DvEl = v;
            dvElSeen[m.Srv.Id + ":" + m.Key] = v;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in dvElSeen) lines.Add(kv.Key + "\t" + kv.Value);
                File.WriteAllLines(DvElPath, lines.ToArray());
            }
            catch (Exception) { }
        }

        // The tag under the resolution badge: DV with its profile (and FEL / MEL once known),
        // HDR10, HLG; nothing for SDR. Public: HDR for Dolby Vision and HDR10.
        static void HdrTag(Graphics g, Movie m, float right, float top)
        {
            string t = "", chip = "";
            if (m.Hdr == "Dolby Vision")
            {
                t = "HDR";
            }
            else if (m.Hdr == "HDR10") t =
                "HDR";
            else if (m.Hdr == "HLG") t = "HLG";
            if (t.Length == 0) return;
            SmoothingMode sm = g.SmoothingMode;
            TextRenderingHint th = g.TextRenderingHint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font f = new Font(BadgeFamily, 11.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fc = new Font(BadgeFamily, 9f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF ts = g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic);
                SizeF cs = chip.Length > 0 ? g.MeasureString(chip, fc, PointF.Empty, StringFormat.GenericTypographic) : SizeF.Empty;
                const float h = 18, padX = 6;
                float chipW = chip.Length > 0 ? cs.Width + 7 : 0;
                float w = padX + ts.Width + (chip.Length > 0 ? 5 + chipW : 0) + padX;
                RectangleF r = new RectangleF(right - w, top, w, h);
                using (GraphicsPath sp = RoundRect(new RectangleF(r.X + 1, r.Y + 2.5f, r.Width, r.Height), 3.5f))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(140, 0, 0, 0))) g.FillPath(sb, sp);
                using (GraphicsPath op = RoundRect(r, 3.5f)) using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, op);
                RectangleF inner = new RectangleF(r.X + 1.5f, r.Y + 1.5f, r.Width - 3, r.Height - 3);
                using (GraphicsPath ip = RoundRect(inner, 2.5f)) using (SolidBrush kb = new SolidBrush(Color.FromArgb(21, 20, 23))) g.FillPath(kb, ip);
                float tx = r.X + padX, ty = r.Y + (h - ts.Height) / 2 + 0.5f;
                using (LinearGradientBrush nb = new LinearGradientBrush(new PointF(0, ty + 1), new PointF(0, ty + ts.Height - 1), Color.Gold, Color.Gold))
                {
                    ColorBlend cb = new ColorBlend(4);
                    cb.Colors = new[] { Color.FromArgb(255, 246, 200), Color.FromArgb(255, 210, 58), Color.FromArgb(201, 138, 0), Color.FromArgb(255, 226, 122) };
                    cb.Positions = new[] { 0f, 0.4f, 0.7f, 1f };
                    nb.InterpolationColors = cb;
                    g.DrawString(t, f, nb, tx, ty, StringFormat.GenericTypographic);
                }
                if (chip.Length > 0)
                {
                    RectangleF c = new RectangleF(tx + ts.Width + 5, inner.Y + 2, chipW, inner.Height - 4);
                    using (GraphicsPath cp = RoundRect(c, 2))
                    using (LinearGradientBrush gb = new LinearGradientBrush(new PointF(c.X, c.Y), new PointF(c.Right, c.Bottom), Color.Gold, Color.Gold))
                    {
                        ColorBlend cb = new ColorBlend(3);
                        cb.Colors = new[] { Color.FromArgb(255, 243, 176), Color.FromArgb(255, 210, 58), Color.FromArgb(217, 154, 0) };
                        cb.Positions = new[] { 0f, 0.4f, 1f };
                        gb.InterpolationColors = cb;
                        g.FillPath(gb, cp);
                    }
                    using (SolidBrush lb = new SolidBrush(Color.FromArgb(21, 20, 23)))
                        g.DrawString(chip, fc, lb, c.X + (c.Width - cs.Width) / 2, c.Y + (c.Height - cs.Height) / 2 + 0.5f, StringFormat.GenericTypographic);
                }
            }
            g.SmoothingMode = sm;
            g.TextRenderingHint = th;
        }

        // 2.88.9: a small gold badge: gold frame, gold numerals on a black panel, the name
        // in black on the gold below, a colour edge on the left (4K red, 1080p blue,
        // 720p / SD yellow) and a white outline.
        static float ResBadge(Graphics g, string res, float right, float top)
        {
            if (string.IsNullOrEmpty(res)) return top;
            string t, sub; Color c;
            if (res == "4K") { t = "4K"; sub = "ULTRA HD"; c = Color.FromArgb(227, 38, 60); }
            else if (res == "1080p") { t = "1080p"; sub = "FULL HD"; c = Color.FromArgb(30, 99, 200); }
            else if (res == "720p") { t = "720p"; sub = "HD"; c = Color.FromArgb(245, 196, 0); }
            else { t = "SD"; sub = "SD"; c = Color.FromArgb(245, 196, 0); }
            SmoothingMode sm = g.SmoothingMode;
            TextRenderingHint th = g.TextRenderingHint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (Font f = new Font(BadgeFamily, 20, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fs = new Font(BadgeFamily, 7.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF sz = g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic);
                SizeF ss = g.MeasureString(sub, fs, PointF.Empty, StringFormat.GenericTypographic);
                const float pad = 2.5f, edge = 4, panelH = 21, labelH = 9.5f;
                float panelW = (float)Math.Ceiling(Math.Max(sz.Width + 10, ss.Width + 6));
                float bw = 1.5f + edge + pad + panelW + pad + 1.5f, bh = 1.5f + pad + panelH + labelH + pad - 1 + 1.5f;
                RectangleF outer = new RectangleF(right - bw, top, bw, bh);
                // shadow, white outline
                using (GraphicsPath sp = RoundRect(new RectangleF(outer.X + 1, outer.Y + 3, outer.Width, outer.Height), 4))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(140, 0, 0, 0))) g.FillPath(sb, sp);
                using (GraphicsPath op = RoundRect(outer, 4)) using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, op);
                RectangleF inner = new RectangleF(outer.X + 1.5f, outer.Y + 1.5f, outer.Width - 3, outer.Height - 3);
                GraphicsState st = g.Save();
                using (GraphicsPath ip = RoundRect(inner, 3))
                {
                    g.SetClip(ip);
                    // gold frame, lit from the top left
                    using (LinearGradientBrush gb = new LinearGradientBrush(new PointF(inner.X, inner.Y), new PointF(inner.Right, inner.Bottom), Color.Gold, Color.Gold))
                    {
                        ColorBlend cb = new ColorBlend(5);
                        cb.Colors = new[] { Color.FromArgb(255, 243, 176), Color.FromArgb(255, 210, 58), Color.FromArgb(217, 154, 0), Color.FromArgb(255, 226, 122), Color.FromArgb(168, 111, 0) };
                        cb.Positions = new[] { 0f, 0.22f, 0.48f, 0.7f, 1f };
                        gb.InterpolationColors = cb;
                        g.FillRectangle(gb, inner);
                    }
                    // the colour edge
                    using (SolidBrush eb = new SolidBrush(c)) g.FillRectangle(eb, inner.X, inner.Y, edge, inner.Height);
                    using (Pen ep = new Pen(Color.FromArgb(180, 255, 255, 255), 1)) g.DrawLine(ep, inner.X + edge, inner.Y, inner.X + edge, inner.Bottom);
                }
                g.Restore(st);
                // black panel with gold numerals
                RectangleF panel = new RectangleF(inner.X + edge + pad, inner.Y + pad, panelW, panelH);
                using (GraphicsPath pp = RoundRect(panel, 1.5f)) using (SolidBrush kb = new SolidBrush(Color.FromArgb(21, 20, 23))) g.FillPath(kb, pp);
                float tx = panel.X + (panel.Width - sz.Width) / 2, ty = panel.Y + (panel.Height - sz.Height) / 2 + 0.5f;
                using (LinearGradientBrush nb = new LinearGradientBrush(new PointF(0, ty + 2), new PointF(0, ty + sz.Height - 2), Color.Gold, Color.Gold))
                {
                    ColorBlend cb = new ColorBlend(5);
                    cb.Colors = new[] { Color.FromArgb(255, 246, 200), Color.FromArgb(255, 210, 58), Color.FromArgb(201, 138, 0), Color.FromArgb(255, 226, 122), Color.FromArgb(176, 120, 0) };
                    cb.Positions = new[] { 0f, 0.35f, 0.62f, 0.85f, 1f };
                    nb.InterpolationColors = cb;
                    g.DrawString(t, f, nb, tx, ty, StringFormat.GenericTypographic);
                }
                // the name in black on the gold
                float sx = panel.X + (panel.Width - ss.Width) / 2, sy = panel.Bottom + (labelH - ss.Height) / 2 + 0.5f;
                using (SolidBrush lb = new SolidBrush(Color.FromArgb(21, 20, 23))) g.DrawString(sub, fs, lb, sx, sy, StringFormat.GenericTypographic);
            }
            g.SmoothingMode = sm;
            g.TextRenderingHint = th;
            return top + 37.5f;
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
                { int ii = i, rw = row; Hit(new RectangleF(x, y, w, h + 60), delegate { navFocus = false; homeRow = rw; homeCol[rw] = ii; }); }   // 0.9.2: mouse
                Poster(g, m, i + row * 3, x, y, w, h, active && !navFocus && i == col);
                bool ep = m.Type == "episode";
                TextBox(g, ep ? m.ShowTitle : m.Title, 20, true, Color.White, new RectangleF(x, y + h + 10, w, 28));
                string sub = row == 0 && m.Duration > 0 && m.Offset > 0 ? DurText(m.Duration - m.Offset) + " left" : (m.Year > 0 && !ep ? m.Year.ToString(CultureInfo.InvariantCulture) : "");
                if (ep) sub = m.Code + (sub.Length > 0 ? "  \u00b7  " + sub : "");
                if (m.Type == "show" && row == 3) sub = m.SeasonNum > 0 ? "Season " + m.SeasonNum.ToString(CultureInfo.InvariantCulture) + " added" : "New episodes";   // 2.71.0
                Txt(g, sub, 18, false, Grey, x, y + h + 38);
            }
        }

        static string DurText(long ms)
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
            g.SetClip(new RectangleF(0, 120, 1920, 900));   // 2.83.0: stops above the help line
            string[] names = { "Continue Watching", "Recently Added Movies", "Next Up", "Recently Added TV" };
            foreach (int r in rows)
            {
                if (y > -400 && y < 980)   // 2.83.0: a row starts only if its heading fits above the help line
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
                { int ii = i; Hit(new RectangleF(x, y, w, h + 60), delegate { navFocus = false; GIdx = ii; }); }   // 0.9.2: mouse
                Poster(g, m, i, x, y, w, h, !navFocus && i == gridIdx);
                TextBox(g, m.Title, 20, true, Color.White, new RectangleF(x, y + h + 10, w, 28));
                string sub = m.Type == "show" && m.LeafCount > 0
                    ? (m.ViewedLeafCount >= m.LeafCount ? "Watched" : (m.LeafCount - m.ViewedLeafCount).ToString(CultureInfo.InvariantCulture) + " unwatched")
                    : (m.Year > 0 ? m.Year.ToString(CultureInfo.InvariantCulture) : "");
                Txt(g, sub, 18, false, Grey, x, y + h + 38);
            }
            using (SolidBrush fb = new SolidBrush(Bg)) g.FillRectangle(fb, 0, 1024, 1920, 56);   // 2.88.0: the footer on its own band (the third row ran under it)
            Txt(g, string.Format(CultureInfo.InvariantCulture, "{0}   \u00b7   {1} of {2}   (OK on the tab: sort)", SortLabel(), gridIdx + 1, movies.Count), 18, false, Grey, 1240, 1036);   // 2.86.1
        }

        void DrawDetails(Graphics g)
        {
            Movie m = det;
            Image art = Img(m, m.Art, 1920, 1080);
            if (art != null) DrawFit(g, art, new RectangleF(0, 0, 1920, 1080), 200, Bg);   // 0.9.4
            Poster(g, m, 0, 96, 140, 360, 540, false);
            float x = 520;
            TextBox(g, m.Title, 56, true, Color.White, new RectangleF(x, 140, 1300, 80));
            List<string> meta = new List<string>();
            if (m.Type == "episode") meta.Add(m.ShowTitle + "  " + m.Code);   // 2.68.0
            if (m.Year > 0 && m.Type != "episode") meta.Add(m.Year.ToString(CultureInfo.InvariantCulture));
            if (m.Duration > 0) meta.Add(DurText(m.Duration));
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
                    { int ii = i; Hit(r, delegate { detOnVersion = false; detBtn = ii; }); }   // 0.9.2: mouse
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
                Hit(r, delegate { detOnVersion = true; });
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

#if PLAYER
        // 2.73.0: Start with Windows: the player in this user's sign-in startup list
        // (HKCU ...\Run), so no administrator rights are needed.
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "ClipGuard Media Player";
        static bool StartWithWindows()
        {
            try { using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(RunKey)) return rk != null && rk.GetValue(RunName) != null; }
            catch (Exception) { return false; }
        }
        void SetStartWithWindows(bool on)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) rk.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else if (rk.GetValue(RunName) != null) rk.DeleteValue(RunName);
                }
            }
            catch (Exception ex) { message = "Windows did not accept the change: " + ex.Message; }
            Invalidate();
        }
#endif

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
                float y = 136 + i * (rows.Length > 14 ? 50 : 54);   // 2.87.1: fourteen rows (2.86.0: 58 apart, 2.75.0: 64); 2.89.1: fifteen, 50 apart
                RectangleF r = new RectangleF(96, y, 1728, 48);
                using (SolidBrush b = new SolidBrush(Card)) g.FillRectangle(b, r);
                Txt(g, p[0], 24, true, Color.White, 126, y + 9);
                TextBox(g, p.Length > 1 ? p[1] : "", 22, false, Grey, new RectangleF(720, y + 11, 1090, 34));
                { int ii = i; Hit(r, delegate { navFocus = false; setIdx = ii; }); }   // 0.9.2: mouse
                if (!navFocus && i == setIdx) Focus(g, r);
            }
            string help = IsLowLatencyRow() ? "Video Processor shows each captured frame as soon as it is ready instead of on a schedule set when the capture starts, so the picture is less behind the sound (about 35 ms in the PC instead of about 150). No frame is dropped. The banner's Latency line shows the time. Off: as before. Works at once (the picture may pause for a moment when it changes)."
                : IsAboutRow() ? "The player's version and build, this PC and its connections. Enter shows them; Copy puts them on the clipboard for a GitHub issue." : SetRow() == 0 ? HelpDelay() : SetRow() == 2
                ? "Plays a flash with a beep. Left / Right moves the sound 10 ms; Enter keeps it, Esc cancels. Do it once with look-ahead Off (F7 menu)."
                : (SetRow() == 3 ? "All servers shows the movies of every server together; or choose one."
                : (SetRow() == 5 ? "Asks for each button in turn (arrows, OK, Back, Play/Pause, Stop, chapters, skip, Menu) and stores what your remote sends. Wait 8 s to skip a button it doesn't have."
#if PLAYER
                : (SetRow() == 9 ? "Five colour themes, all dark for a theatre; the change shows straight away. Focus always has a white outline."
                : (false ? ""
                : (SetRow() == 12 ? "A soft tick as you move, a click to select and a lower one to go back, in the library and menus only (never during a film or Video Processor)."
                : (false ? ""
                : (SetRow() == 8 ? "On: the player opens when you sign in to Windows (with Picture > General > Start in, straight to the library or Video Processor). For no keyboard at all, Windows must also sign in by itself."
                : (SetRow() == 7 ? "Video Processor plays the capture card's PCM sound (a PS5 game set to Linear PCM) on this PC, held back with the picture. Raise it if the sound is early, lower it if late. Dolby / DTS bitstream on the card is muted."
                : ""))))))));
#else
                : ""));
#endif
            Txt(g, help, 20, false, Grey, 96, 926);
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
