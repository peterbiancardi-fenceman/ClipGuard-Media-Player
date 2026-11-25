// ClipGuard Meter auto-cal (2.48.0, ClipGuard Test package only).
//
// L = auto-cal (after aiming). Pick the projector, gamma and colour space;
// set white by eye; the meter reads the native white and a few hundred
// patches through the projector's .ccss (titan.ccss / valerion.ccss next to
// ClipGuardMeter.exe); ArgyllCMS colprof + collink build a 3D LUT around that
// white (relative colorimetric), saved as titan.cube / valerion.cube next to
// clipguard.ini, where the renderer loads it when the ClipGuard menu's LUT row is set to it (2.50.0). The
// meter then applies the LUT to its own check patches the way the renderer
// does, measures them, and writes a PDF report.
//
// C# 5 (no string interpolation): the Test package is built with the
// .NET Framework csc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClipGuard
{
    // ---- colour maths -----------------------------------------------------------
    static class Cal
    {
        public static double[] Xyz(double x, double y, double Y) { return new double[] { x / y * Y, Y, (1 - x - y) / y * Y }; }

        public static double[] Mul(double[,] m, double[] v)
        {
            return new double[] {
                m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
                m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
                m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2] };
        }

        public static double[,] Inv(double[,] m)
        {
            double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], i = m[2, 2];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            double[,] r = new double[3, 3];
            r[0, 0] = A / det; r[0, 1] = -(b * i - c * h) / det; r[0, 2] = (b * f - c * e) / det;
            r[1, 0] = B / det; r[1, 1] = (a * i - c * g) / det; r[1, 2] = -(a * f - c * d) / det;
            r[2, 0] = C / det; r[2, 1] = -(a * h - b * g) / det; r[2, 2] = (a * e - b * d) / det;
            return r;
        }

        // RGB -> XYZ for primaries (xy) and a white (XYZ), white Y as given.
        public static double[,] RgbToXyz(double[][] prim, double[] white)
        {
            double[,] m = new double[3, 3];
            for (int k = 0; k < 3; k++)
            {
                double[] p = Xyz(prim[k][0], prim[k][1], 1);
                m[0, k] = p[0]; m[1, k] = p[1]; m[2, k] = p[2];
            }
            double[] s = Mul(Inv(m), white);
            for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) m[r, k] *= s[k];
            return m;
        }

        public static readonly double[][] P709 = { new double[] { 0.64, 0.33 }, new double[] { 0.30, 0.60 }, new double[] { 0.15, 0.06 } };
        public static readonly double[][] P2020 = { new double[] { 0.708, 0.292 }, new double[] { 0.170, 0.797 }, new double[] { 0.131, 0.046 } };
        public static readonly double[] D65 = Xyz(0.3127, 0.3290, 1);

        static double F(double t) { return t > 216.0 / 24389.0 ? Math.Pow(t, 1.0 / 3) : (24389.0 / 27.0 * t + 16) / 116; }
        public static double[] Lab(double[] X, double[] W)
        {
            double fx = F(X[0] / W[0]), fy = F(X[1] / W[1]), fz = F(X[2] / W[2]);
            return new double[] { 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz) };
        }

        // CIEDE2000.
        public static double DE2000(double[] l1, double[] l2)
        {
            double L1 = l1[0], a1 = l1[1], b1 = l1[2], L2 = l2[0], a2 = l2[1], b2 = l2[2];
            double C1 = Math.Sqrt(a1 * a1 + b1 * b1), C2 = Math.Sqrt(a2 * a2 + b2 * b2), Cm = (C1 + C2) / 2;
            double G = 0.5 * (1 - Math.Sqrt(Math.Pow(Cm, 7) / (Math.Pow(Cm, 7) + Math.Pow(25, 7))));
            double a1p = (1 + G) * a1, a2p = (1 + G) * a2;
            double C1p = Math.Sqrt(a1p * a1p + b1 * b1), C2p = Math.Sqrt(a2p * a2p + b2 * b2);
            double h1p = Math.Atan2(b1, a1p) * 180 / Math.PI; if (h1p < 0) h1p += 360;
            double h2p = Math.Atan2(b2, a2p) * 180 / Math.PI; if (h2p < 0) h2p += 360;
            double dLp = L2 - L1, dCp = C2p - C1p, dhp;
            if (C1p * C2p == 0) dhp = 0;
            else if (Math.Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
            else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
            else dhp = h2p - h1p + 360;
            double dHp = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(dhp / 2 * Math.PI / 180);
            double Lmp = (L1 + L2) / 2, Cmp = (C1p + C2p) / 2, hmp;
            if (C1p * C2p == 0) hmp = h1p + h2p;
            else if (Math.Abs(h1p - h2p) <= 180) hmp = (h1p + h2p) / 2;
            else if (h1p + h2p < 360) hmp = (h1p + h2p + 360) / 2;
            else hmp = (h1p + h2p - 360) / 2;
            double T = 1 - 0.17 * Math.Cos((hmp - 30) * Math.PI / 180) + 0.24 * Math.Cos(2 * hmp * Math.PI / 180)
                     + 0.32 * Math.Cos((3 * hmp + 6) * Math.PI / 180) - 0.20 * Math.Cos((4 * hmp - 63) * Math.PI / 180);
            double dTh = 30 * Math.Exp(-Math.Pow((hmp - 275) / 25, 2));
            double Rc = 2 * Math.Sqrt(Math.Pow(Cmp, 7) / (Math.Pow(Cmp, 7) + Math.Pow(25, 7)));
            double Sl = 1 + 0.015 * Math.Pow(Lmp - 50, 2) / Math.Sqrt(20 + Math.Pow(Lmp - 50, 2));
            double Sc = 1 + 0.045 * Cmp, Sh = 1 + 0.015 * Cmp * T;
            double Rt = -Math.Sin(2 * dTh * Math.PI / 180) * Rc;
            return Math.Sqrt(Math.Pow(dLp / Sl, 2) + Math.Pow(dCp / Sc, 2) + Math.Pow(dHp / Sh, 2) + Rt * (dCp / Sc) * (dHp / Sh));
        }

        static readonly double[,] MBrad = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };
        // Bradford chromatic adaptation of X from white ws to white wd.
        public static double[] Bradford(double[] X, double[] ws, double[] wd)
        {
            double[] s = Mul(MBrad, ws), d = Mul(MBrad, wd), c = Mul(MBrad, X);
            for (int k = 0; k < 3; k++) c[k] *= d[k] / s[k];
            return Mul(Inv(MBrad), c);
        }

        public static double[] Xy(double[] X) { double s = X[0] + X[1] + X[2]; return s <= 0 ? new double[] { 0.3127, 0.329 } : new double[] { X[0] / s, X[1] / s }; }
        public static double[] Uv(double[] X) { double d = X[0] + 15 * X[1] + 3 * X[2]; return d <= 0 ? new double[] { 0, 0 } : new double[] { 4 * X[0] / d, 9 * X[1] / d }; }

        // McCamy's approximation, good near the daylight locus.
        public static double Cct(double[] X)
        {
            double[] xy = Xy(X);
            double n = (xy[0] - 0.3320) / (0.1858 - xy[1]);
            return 449 * n * n * n + 3525 * n * n + 6823.3 * n + 5520.33;
        }

        // Distance from D65 in u'v'.
        public static double DuvD65(double[] X) { double[] a = Uv(X), b = Uv(D65); return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])); }
    }

    // ---- .cube reader, applied like the renderer does (trilinear, red fastest) --------
    class Cube
    {
        public int N;
        float[] d;
        public static Cube Load(string path)
        {
            Cube c = new Cube();
            List<float> v = new List<float>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string s = raw.Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                if (s.StartsWith("LUT_3D_SIZE")) { c.N = int.Parse(s.Substring(11).Trim(), CultureInfo.InvariantCulture); continue; }
                if (char.IsLetter(s[0])) continue;
                string[] p = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 3) continue;
                for (int k = 0; k < 3; k++) v.Add(float.Parse(p[k], CultureInfo.InvariantCulture));
            }
            if (c.N < 2 || v.Count != c.N * c.N * c.N * 3) throw new Exception("bad .cube file");
            c.d = v.ToArray();
            return c;
        }
        double At(int r, int g, int b, int k) { return d[((b * N + g) * N + r) * 3 + k]; }

        // Writes a copy where the LUT fades to no change below lo..hi (input's brightest channel).
        // 2.88.7: edgeLo..edgeHi (saturation, (max-min)/max): colours at the edge of what the
        // projector can show fade to its own colour, so a pure BT.2020 blue it can't reach
        // stays its most saturated blue instead of being pulled off hue; less saturated
        // colours keep the full correction. edgeLo >= 1 turns it off.
        public static void BlendDark(string src, string dst, double lo, double hi, double edgeLo = 2, double edgeHi = 3)
        {
            Cube c = Load(src);
            int N = c.N;
            bool edge = edgeLo < 1;
            StringBuilder sb = new StringBuilder("# ClipGuard: ArgyllCMS LUT, " + (edge ? "edge colours kept native, " : "") + "black and near-black left native\nLUT_3D_SIZE " + N + "\n");
            for (int b = 0; b < N; b++)
                for (int g = 0; g < N; g++)
                    for (int r = 0; r < N; r++)
                    {
                        double[] inp = { r / (double)(N - 1), g / (double)(N - 1), b / (double)(N - 1) };
                        double t = Math.Max(inp[0], Math.Max(inp[1], inp[2]));
                        double u = Math.Max(0, Math.Min(1, (t - lo) / (hi - lo)));
                        double w = u * u * (3 - 2 * u);
                        if (edge && t > 0)
                        {
                            double sat = (t - Math.Min(inp[0], Math.Min(inp[1], inp[2]))) / t;
                            double e = Math.Max(0, Math.Min(1, (sat - edgeLo) / (edgeHi - edgeLo)));
                            w *= 1 - e * e * (3 - 2 * e);
                        }
                        double[] o = new double[3];
                        for (int k = 0; k < 3; k++) o[k] = inp[k] + w * (c.At(r, g, b, k) - inp[k]);
                        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.000000} {1:0.000000} {2:0.000000}\n", o[0], o[1], o[2]);
                    }
            File.WriteAllText(dst, sb.ToString());
        }
        // 2.97.0: the white set by the LUT: every output scaled per channel (k <= 1), so the
        // projector's own white lands on the target white; the rest of the LUT is unchanged.
        public static void ScaleOut(string src, string dst, double[] k)
        {
            Cube c = Load(src);
            int N = c.N;
            StringBuilder sb = new StringBuilder(string.Format(CultureInfo.InvariantCulture,
                "# ClipGuard: ArgyllCMS LUT, white set by the LUT (R x{0:0.0000}, G x{1:0.0000}, B x{2:0.0000})\nLUT_3D_SIZE {3}\n", k[0], k[1], k[2], N));
            for (int b = 0; b < N; b++)
                for (int g = 0; g < N; g++)
                    for (int r = 0; r < N; r++)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:0.000000} {1:0.000000} {2:0.000000}\n",
                            Math.Max(0, Math.Min(1, c.At(r, g, b, 0) * k[0])), Math.Max(0, Math.Min(1, c.At(r, g, b, 1) * k[1])), Math.Max(0, Math.Min(1, c.At(r, g, b, 2) * k[2])));
            File.WriteAllText(dst, sb.ToString());
        }

        public double[] Apply(double r, double g, double b)
        {
            double[] p = { r * (N - 1), g * (N - 1), b * (N - 1) };
            int[] i = new int[3]; double[] f = new double[3];
            for (int k = 0; k < 3; k++) { i[k] = Math.Max(0, Math.Min(N - 2, (int)Math.Floor(p[k]))); f[k] = p[k] - i[k]; }
            double[] o = new double[3];
            for (int dr = 0; dr < 2; dr++)
                for (int dg = 0; dg < 2; dg++)
                    for (int db = 0; db < 2; db++)
                    {
                        double w = (dr == 1 ? f[0] : 1 - f[0]) * (dg == 1 ? f[1] : 1 - f[1]) * (db == 1 ? f[2] : 1 - f[2]);
                        for (int k = 0; k < 3; k++) o[k] += w * At(i[0] + dr, i[1] + dg, i[2] + db, k);
                    }
            return o;
        }
    }

    // ---- a small PDF writer (text, lines, rectangles; Helvetica) ----------------------
    class Pdf
    {
        public const double PW = 612, PH = 792;   // US Letter, points
        readonly List<StringBuilder> pages = new List<StringBuilder>();
        StringBuilder cur;
        public Pdf() { NewPage(); }
        public void NewPage() { cur = new StringBuilder(); pages.Add(cur); }
        static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        static string Esc(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '(' || c == ')' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32 || c > 126) b.Append('?');
                else b.Append(c);
            }
            return b.ToString();
        }
        // y is from the top of the page.
        public void Text(double x, double y, double size, string s, bool bold, Color c)
        {
            cur.AppendFormat("BT /{0} {1} Tf {2} {3} {4} rg {5} {6} Td ({7}) Tj ET\n", bold ? "F2" : "F1", N(size),
                N(c.R / 255.0), N(c.G / 255.0), N(c.B / 255.0), N(x), N(PH - y), Esc(s));
        }
        public void Text(double x, double y, double size, string s) { Text(x, y, size, s, false, Color.Black); }
        public void Rect(double x, double y, double w, double h, Color? fill, Color? stroke, double lw)
        {
            if (fill.HasValue) cur.AppendFormat("{0} {1} {2} rg ", N(fill.Value.R / 255.0), N(fill.Value.G / 255.0), N(fill.Value.B / 255.0));
            if (stroke.HasValue) cur.AppendFormat("{0} {1} {2} RG {3} w ", N(stroke.Value.R / 255.0), N(stroke.Value.G / 255.0), N(stroke.Value.B / 255.0), N(lw));
            cur.AppendFormat("{0} {1} {2} {3} re {4}\n", N(x), N(PH - y - h), N(w), N(h), fill.HasValue && stroke.HasValue ? "B" : (fill.HasValue ? "f" : "S"));
        }
        public void Line(double x1, double y1, double x2, double y2, Color c, double lw, bool dashed)
        {
            cur.AppendFormat("{0} {1} {2} RG {3} w {4} {5} {6} m {7} {8} l S [] 0 d\n", N(c.R / 255.0), N(c.G / 255.0), N(c.B / 255.0), N(lw),
                dashed ? "[4 3] 0 d" : "[] 0 d", N(x1), N(PH - y1), N(x2), N(PH - y2));
        }
        public void Save(string path)
        {
            List<string> objs = new List<string>();
            int nPages = pages.Count;
            // 1 catalog, 2 pages, 3 F1, 4 F2, then page+content pairs
            objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
            StringBuilder kids = new StringBuilder();
            for (int i = 0; i < nPages; i++) kids.AppendFormat("{0} 0 R ", 5 + i * 2);
            objs.Add("<< /Type /Pages /Kids [" + kids + "] /Count " + nPages + " >>");
            objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            for (int i = 0; i < nPages; i++)
            {
                objs.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + N(PW) + " " + N(PH) + "] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents " + (6 + i * 2) + " 0 R >>");
                string content = pages[i].ToString();
                objs.Add("<< /Length " + Encoding.ASCII.GetByteCount(content) + " >>\nstream\n" + content + "endstream");
            }
            using (MemoryStream ms = new MemoryStream())
            {
                Action<string> w = s => { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); };
                w("%PDF-1.4\n");
                List<long> offs = new List<long>();
                for (int i = 0; i < objs.Count; i++)
                {
                    offs.Add(ms.Position);
                    w((i + 1) + " 0 obj\n" + objs[i] + "\nendobj\n");
                }
                long xref = ms.Position;
                w("xref\n0 " + (objs.Count + 1) + "\n0000000000 65535 f \n");
                foreach (long o in offs) w(o.ToString("0000000000") + " 00000 n \n");
                w("trailer\n<< /Size " + (objs.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                File.WriteAllBytes(path, ms.ToArray());
            }
        }
    }

    class CalStop : Exception { public CalStop(string m) : base(m) { } }

    class CalPatch
    {
        public string Name;
        public int R, G, B;
        public double[] Before, After;   // measured XYZ (cd/m2)
        public double DeBefore, DeAfter;
    }

    // 2.66.0: the player's "Calibrate a projector" runs the meter with these (command line):
    // any projector by name, its .ccss or none, the LUT and report saved in OutDir.
    static class CalApp
    {
        public static bool On;
        public static string Name = "", Ccss = "", OutDir = "", ArgyllBin = "", Screen = "";
        public static string File { get { return Safe(Name); } }
        public static string Safe(string n)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in n.Trim()) b.Append(Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return b.Length > 0 ? b.ToString() : "projector";
        }
        // --cal "<name>" --ccss "<path>|none" --out "<dir>" --argyll "<bin dir>" --screen "<\.\DISPLAYn>"
        public static void Parse(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant(), v = args[i + 1];
                if (a == "--cal") { On = true; Name = v; i++; }
                else if (a == "--ccss") { Ccss = string.Equals(v, "none", StringComparison.OrdinalIgnoreCase) ? "" : v; i++; }
                else if (a == "--out") { OutDir = v; i++; }
                else if (a == "--argyll") { ArgyllBin = v; i++; }
                else if (a == "--screen") { Screen = v; i++; }
            }
        }
    }

    partial class MeterForm
    {
        // ---- setup screen -------------------------------------------------------------
        string CalName() { return CalApp.On ? CalApp.Name : CalProjNames[calProj]; }
        string CalShortName() { return CalApp.On ? CalApp.Name : CalProjShort[calProj]; }
        string CalFile() { return CalApp.On ? CalApp.File : CalProjFiles[calProj]; }
        // The .ccss to use: "" = none (the meter's standard reading).
        string CalCcss() { return CalApp.On ? CalApp.Ccss : Path.Combine(appDir, CalProjFiles[calProj] + ".ccss"); }

        static readonly string[] CalProjNames = { "Titan Noir Max", "Valerion Max" };
        static readonly string[] CalProjShort = { "Titan", "Valerion" };
        static readonly string[] CalProjFiles = { "titan", "valerion" };
        static readonly string[] CalGammaNames = { "2.2", "2.4", "BT.1886" };
        static readonly string[] CalSpaceNames = { "BT.709", "BT.2020" };
        volatile bool calSetup;
        int calRow, calProj, calGamma, calSpace = 1, calRefresh;   // calRefresh: 0 = normal, 1 = refresh mode (spotread -Y r)
        int calWhite;   // 2.96.0: 0 = perceptual white point (set with the live balance meter), 1 = as set by eye; 2.97.0: 2 = perceptual, set by the LUT
        const double PerceptualX = 0.3034, PerceptualY = 0.3061;   // CIE 1931 2 degree
        static readonly string[] CalModeNames = { "Normal", "Refresh (synced to the projector)" };
        volatile bool calWaiting;          // the run waits for Enter (or Esc)
        readonly AutoResetEvent calGo = new AutoResetEvent(false);
        volatile bool calEsc;
        volatile bool calSkip;             // 2.88.6: S / Right on a check screen = carry on

        string CalSetupText()
        {
            StringBuilder b = new StringBuilder("AUTO-CAL\n\n");
            string[] rows = {
                "Projector:     " + CalName() + (CalApp.On ? "   (correction: " + (CalApp.Ccss.Length > 0 ? Path.GetFileName(CalApp.Ccss) : "none") + ")" : ""),
                "Gamma:         " + CalGammaNames[calGamma],
                "Colour space:  " + CalSpaceNames[calSpace],
                "White point:   " + (calWhite == 0 ? string.Format(CultureInfo.InvariantCulture, "Perceptual (x {0:0.0000}, y {1:0.0000}), set with the live meter", PerceptualX, PerceptualY)
                    : calWhite == 2 ? string.Format(CultureInfo.InvariantCulture, "Perceptual (x {0:0.0000}, y {1:0.0000}), set by the LUT (leave the projector's white as it is)", PerceptualX, PerceptualY)
                    : "As set (by eye)"),
                "Meter mode:    " + CalModeNames[calRefresh] };
            for (int i = 0; i < rows.Length; i++) b.Append(i == calRow ? ">  " : "    ").Append(rows[i]).Append('\n');
            b.Append("\nUp / Down = choose a row   Left / Right = change   Enter = start   Esc = back\n");
            b.Append("N = noise check (which meter mode reads steadier, about 1 minute)\n\n");
            if (CalApp.On) b.Append(CalApp.Ccss.Length > 0 ? "Uses " + Path.GetFileName(CalApp.Ccss) : "No .ccss correction: accuracy depends on how well the meter matches this projector's light")
                .Append(". Saves " + CalApp.File + ".cube and the report in " + CalApp.OutDir + ".\n");
            else b.Append("Uses " + CalProjFiles[calProj] + ".ccss next to ClipGuardMeter.exe and saves " + CalProjFiles[calProj] + ".cube.\n");
            b.Append("Turn the projector's laser dimming (DBLE / EBL) OFF before starting.\n");
            b.Append("Meter: on the tripod pointing at the screen, no diffuser (auto-cal starts with its own aiming).");
            return b.ToString();
        }

        // Keys for the auto-cal; true when handled.
        bool CalKey(Keys k)
        {
            if (calWaiting)
            {
                if (k == Keys.Enter || k == Keys.Return) { calEsc = false; calSkip = false; calGo.Set(); return true; }
                if (k == Keys.S || k == Keys.Right) { calEsc = false; calSkip = true; calGo.Set(); return true; }
                if (k == Keys.Escape) { calEsc = true; cancel = true; calGo.Set(); return true; }
                return true;
            }
            if (calSetup)
            {
                if (k == Keys.Up) calRow = (calRow + 4) % 5;
                else if (k == Keys.Down) calRow = (calRow + 1) % 5;
                else if (k == Keys.Left || k == Keys.Right)
                {
                    int d = k == Keys.Right ? 1 : -1;
                    if (calRow == 0) { if (!CalApp.On) calProj = (calProj + 2 + d) % 2; }
                    else if (calRow == 1) calGamma = (calGamma + 3 + d) % 3;
                    else if (calRow == 2) calSpace = (calSpace + 2 + d) % 2;
                    else if (calRow == 3) calWhite = (calWhite + 3 + d) % 3;   // 2.96.0; 2.97.0: three choices
                    else calRefresh = 1 - calRefresh;
                }
                else if (k == Keys.N)
                {
                    calSetup = false;
                    idle = false;
                    cancel = false;
                    worker = new Thread(NoiseCheck);
                    worker.IsBackground = true;
                    worker.Start();
                    return true;
                }
                else if (k == Keys.Escape) { calSetup = false; if (CalApp.On) { Close(); return true; } SetIdle(Intro()); return true; }
                else if (k == Keys.Enter || k == Keys.Return)
                {
                    calSetup = false;
                    idle = false;
                    cancel = false;
                    worker = new Thread(CalRun);
                    worker.IsBackground = true;
                    worker.Start();
                    return true;
                }
                else return true;
                SetIdle(CalSetupText());
                return true;
            }
            if (idle && k == Keys.L)
            {
                calSetup = true;
                calRow = 0;
                SetIdle(CalSetupText());
                return true;
            }
            return false;
        }

        void CalStartMeter(Spotread sp, string ccss, bool refresh)
        {
            // 2.66.0: no .ccss = the meter's standard reading.
            sp.Args = (MeterArgs(false) + (refresh ? " -Y r" : "") + (ccss.Length > 0 ? " -X \"" + ccss + "\"" : "")).Trim();   // off the screen, no diffuser
            string key = Native.ReadIni(seedIni, "Meter", "ReadKey", "space").Trim();
            sp.Key = key.Equals("space", StringComparison.OrdinalIgnoreCase) || key.Length == 0 ? " " : key.Substring(0, 1);
            clock.Restart();
            string err;
            if (!sp.Start(out err)) { sp.Dispose(); sp.OneShot = true; }
        }

        // Noise check: a 50% grey read 10 times in each meter mode; the spread shows which is steadier.
        void NoiseCheck()
        {
            List<string> r = new List<string>();
            csv = new StringBuilder("step,scene,bg,patch,box_level,box_area,box_pos,ramp,rep,t_switch,t_start,t_end,ok,X,Y,Z,error\n");
            try
            {
                string ccss = CalCcss();
                if (ccss.Length > 0 && !File.Exists(ccss)) throw new CalStop("Stopped: " + Path.GetFileName(ccss) + " was not found.");
                for (int mode = 0; mode < 2; mode++)
                {
                    Spotread sp = new Spotread(clock);
                    reader = sp;
                    sp.Exe = Spotread.Find(seedIni);
                    if (sp.Exe.Length == 0) throw new CalStop("Stopped: ArgyllCMS spotread.exe was not found.");
                    try
                    {
                        Status("Noise check: " + CalModeNames[mode] + " mode");
                        CalStartMeter(sp, ccss, mode == 1);
                        Show(Full("noise", 128, 128, 128, null));
                        Thread.Sleep(1000);
                        List<double[]> v = new List<double[]>();
                        for (int i = 0; i < 10; i++)
                        {
                            if (cancel) throw new OperationCanceledException();
                            MReading m = sp.Read(60000);
                            Row("N" + mode, Full("noise", 128, 128, 128, null), i, -1, m);
                            if (m.Ok) { double[] xy = Cal.Xy(new double[] { m.X, m.Y, m.Z }); v.Add(new double[] { xy[0], xy[1], m.Y }); }
                        }
                        if (v.Count < 3) { r.Add(CalModeNames[mode] + ": too few readings (this mode may not work with this meter)."); continue; }
                        double[] mean = new double[3], sd = new double[3];
                        foreach (double[] a in v) for (int k = 0; k < 3; k++) mean[k] += a[k] / v.Count;
                        foreach (double[] a in v) for (int k = 0; k < 3; k++) sd[k] += (a[k] - mean[k]) * (a[k] - mean[k]) / (v.Count - 1);
                        r.Add(string.Format(CultureInfo.InvariantCulture, "{0}: x {1:0.0000} +/- {2:0.0000}   y {3:0.0000} +/- {4:0.0000}   brightness +/- {5:0.00}%",
                            CalModeNames[mode], mean[0], Math.Sqrt(sd[0]), mean[1], Math.Sqrt(sd[1]), 100 * Math.Sqrt(sd[2]) / mean[2]));
                    }
                    finally { sp.Dispose(); }
                }
            }
            catch (CalStop ex) { r.Add(ex.Message); }
            catch (OperationCanceledException) { r.Add("Stopped."); }
            catch (Exception ex) { r.Add("Error: " + ex.Message); }
            finally { Show(new MScene { Id = "idle", Bg = 16, Patch = -1 }); }
            calSetup = true;
            SetIdle("NOISE CHECK (50% grey, 10 readings each; smaller +/- = steadier)\n\n" + string.Join("\n", r.ToArray()) + "\n\n" + CalSetupText());
        }

        // Waits for Enter while a full-screen scene is shown; false on Esc.
        bool CalWait(MScene sc)
        {
            calEsc = false;
            calSkip = false;
            calGo.Reset();
            calWaiting = true;
            Show(sc);
            calGo.WaitOne();
            calWaiting = false;
            return !calEsc;
        }

        // ---- 2.88.6: level checks before the white: black (brightness, low-end tint) and
        // white (contrast, top-end clipping), each a pattern with its instructions; the
        // white one is then measured (250 and 254 against 255) and shown again until the
        // top end is no longer clipping, or S carries on.
        void CalLevelChecks()
        {
            Size sz = (Size)Invoke(new Func<Size>(() => ClientSize));
            // (the pictures are not disposed here: the window may still paint the last one)
            if (!CalWait(new MScene { Id = "check_black", Canvas = CalPattern(false, sz.Width, sz.Height, null), Patch = -1 })) throw new OperationCanceledException();
            string warn = null;
            while (true)
            {
                if (!CalWait(new MScene { Id = "check_white", Canvas = CalPattern(true, sz.Width, sz.Height, warn), Patch = -1 })) throw new OperationCanceledException();
                if (calSkip) return;
                Status("Measuring the top end...");
                MReading r255 = CalRead("top_255", 255, 255, 255, 1.0, 3);
                MReading r250 = CalRead("top_250", 250, 250, 250, 0.5, 3);
                MReading r254 = CalRead("top_254", 254, 254, 254, 0.5, 3);
                if (!r255.Ok || !r250.Ok || r255.Y <= 0)
                {
                    warn = "The meter gave no reading (" + (r255.Ok ? r250.Error : r255.Error) + "). Enter = measure again,  S = carry on anyway.";
                    continue;
                }
                double q250 = r250.Y / r255.Y, q254 = r254.Ok ? r254.Y / r255.Y : double.NaN;
                double e250 = Math.Pow(250 / 255.0, 2.2), e254 = Math.Pow(254 / 255.0, 2.2);
                csv.AppendFormat(CultureInfo.InvariantCulture, "# top end: 250 = {0:0.0000} of 255 (expected {1:0.0000}), 254 = {2:0.0000} (expected {3:0.0000})\n", q250, e250, q254, e254);
                // the top end is squeezed when 250 sits less than 3/4 of its expected step below
                // white (the 21 Nov run: about 2/3, with 247 to 255 bunched together)
                if (1 - q250 >= (1 - e250) * 0.75) return;
                warn = string.Format(CultureInfo.InvariantCulture,
                    "The top end is still squeezed: 250 reads {0:0.0}% of white (about {1:0.0}% expected){2}. Lower contrast a step (or the gain of a tinted colour), then Enter = measure again,  S = carry on anyway.",
                    q250 * 100, e250 * 100, double.IsNaN(q254) ? "" : string.Format(CultureInfo.InvariantCulture, ", 254 reads {0:0.0}% (about {1:0.0}%)", q254 * 100, e254 * 100));
            }
        }

        // ---- 2.96.0: the perceptual white point: the white window (where the meter reads) with a
        // small live meter beside it: R, G and B against the target white, x / y, and the
        // brightness, read continuously while the projector's gains are adjusted. Enter goes on.
        void CalWhitePoint(Spotread sp)
        {
            Size sz = (Size)Invoke(new Func<Size>(() => ClientSize));
            calEsc = false;
            calGo.Reset();
            calWaiting = true;
            Bitmap prev = null, prev2 = null;
            try
            {
                MReading m = null;
                while (true)
                {
                    Bitmap bm = CalWhiteCanvas(sz.Width, sz.Height, m);
                    Show(new MScene { Id = "white_point", Canvas = bm, Patch = -1 });
                    if (prev2 != null) prev2.Dispose();   // two back: never the one being painted
                    prev2 = prev; prev = bm;
                    if (calGo.WaitOne(0)) break;
                    m = sp.Read(30000);
                    if (m.Ok) Row("WP", Full("white_point", 255, 255, 255, null), 0, -1, m);
                    if (cancel) break;
                }
            }
            finally { calWaiting = false; }
            if (calEsc || cancel) throw new OperationCanceledException();
        }

        static Bitmap CalWhiteCanvas(int W, int H, MReading m)
        {
            Bitmap bm = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bm))
            using (Font ft = new Font("Segoe UI", Math.Max(12, H / 60f), FontStyle.Bold))
            using (Font fv = new Font("Segoe UI", Math.Max(11, H / 70f), FontStyle.Bold))
            using (Font fs = new Font("Segoe UI", Math.Max(10, H / 80f)))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                float side = (float)Math.Sqrt(CalWindow * W * H);
                float wx = (W - side) / 2f, wy = (H - side) / 2f;
                g.FillRectangle(Brushes.White, wx, wy, side, side);   // the same window the meter reads on every patch
                // the panel, beside the window
                float px = wx + side + W * 0.03f, pw = Math.Min(W * 0.24f, W - px - W * 0.02f), ph = side, py = wy;
                using (SolidBrush pb = new SolidBrush(Color.FromArgb(26, 26, 30))) g.FillRectangle(pb, px, py, pw, ph);
                using (Pen pp = new Pen(Color.FromArgb(120, 120, 120), Math.Max(1, H / 1080f))) g.DrawRectangle(pp, px, py, pw, ph);
                float x = px + pw * 0.06f, y = py + ph * 0.05f, lh = H / 34f;
                g.DrawString("WHITE POINT", ft, Brushes.White, x, y);
                y += lh * 1.1f;
                using (SolidBrush gb = new SolidBrush(Color.FromArgb(170, 170, 170)))
                    g.DrawString(string.Format(CultureInfo.InvariantCulture, "target  x {0:0.0000}   y {1:0.0000}", PerceptualX, PerceptualY), fs, gb, x, y);
                y += lh * 1.2f;
                double[] bal = null; double[] xy = null;
                if (m != null && m.Ok && m.Y > 0)
                {
                    double[] tw = { PerceptualX / PerceptualY, 1, (1 - PerceptualX - PerceptualY) / PerceptualY };
                    double[] rgb = Cal.Mul(Cal.Inv(Cal.RgbToXyz(Cal.P709, tw)), new double[] { m.X / m.Y, 1, m.Z / m.Y });
                    double avg = (rgb[0] + rgb[1] + rgb[2]) / 3;
                    bal = new double[3];
                    for (int k = 0; k < 3; k++) bal[k] = (rgb[k] / avg - 1) * 100;
                    xy = Cal.Xy(new double[] { m.X, m.Y, m.Z });
                }
                // R, G, B: a bar either side of the middle line, +-10% across, the value written beside it
                string[] nm = { "R", "G", "B" };
                Color[] col = { Color.FromArgb(235, 45, 45), Color.FromArgb(40, 190, 80), Color.FromArgb(55, 115, 255) };
                float bx = x + pw * 0.10f, bw = pw * 0.56f, bh = lh * 0.7f, mid = bx + bw / 2f;
                for (int k = 0; k < 3; k++)
                {
                    g.DrawString(nm[k], fv, Brushes.White, x, y - bh * 0.15f);
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(50, 50, 56))) g.FillRectangle(tb, bx, y, bw, bh);
                    if (bal != null)
                    {
                        float v = (float)Math.Max(-10, Math.Min(10, bal[k])) / 10f * (bw / 2f);
                        RectangleF r = v >= 0 ? new RectangleF(mid, y, v, bh) : new RectangleF(mid + v, y, -v, bh);
                        if (r.Width > 0.5f)
                        {
                            using (SolidBrush cb = new SolidBrush(col[k])) g.FillRectangle(cb, r);
                            using (Pen wp = new Pen(Color.White, Math.Max(1, H / 1080f))) g.DrawRectangle(wp, r.X, r.Y, r.Width, r.Height);
                        }
                        g.DrawString(bal[k].ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%", fv, Brushes.White, bx + bw + pw * 0.03f, y - bh * 0.15f);
                    }
                    using (Pen lp = new Pen(Color.White, Math.Max(2, H / 540f))) g.DrawLine(lp, mid, y - bh * 0.2f, mid, y + bh * 1.2f);
                    y += lh * 1.15f;
                }
                y += lh * 0.3f;
                if (bal != null)
                {
                    g.DrawString(string.Format(CultureInfo.InvariantCulture, "x {0:0.0000}   y {1:0.0000}", xy[0], xy[1]), fv, Brushes.White, x, y);
                    y += lh;
                    g.DrawString(string.Format(CultureInfo.InvariantCulture, "Brightness  {0:0.0} nits", m.Y), fv, Brushes.White, x, y);
                    y += lh * 1.2f;
                    bool on = Math.Abs(bal[0]) <= 1 && Math.Abs(bal[1]) <= 1 && Math.Abs(bal[2]) <= 1;
                    using (SolidBrush sb = new SolidBrush(on ? Color.FromArgb(255, 212, 0) : Color.FromArgb(200, 200, 200)))
                        g.DrawString(on ? "ON TARGET (all within 1%)" : "Adjust until all three are within 1%", fv, sb, x, y);
                }
                else
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(200, 200, 200)))
                        g.DrawString(m == null ? "Reading..." : "No reading: " + m.Error, fv, sb, x, y);
                // the instructions, under the window
                string ins = "Set the projector's white with its R, G, B gains (and offsets for the low end) until the bars sit on the middle line.\n"
                    + "A bar to the right is too much of that colour, to the left too little.     Enter = done     Esc = stop";
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
                using (SolidBrush ib = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    g.DrawString(ins, fs, ib, new RectangleF(W * 0.08f, wy + side + H * 0.05f, W * 0.84f, H * 0.15f), sf);
            }
            return bm;
        }

        // The pattern screens: patches on a black or white surround in a band across the
        // centre, black around it (a small bright area, as the measuring windows are), the
        // levels written under them, and the instructions below.
        static Bitmap CalPattern(bool white, int W, int H, string warn)
        {
            int[] lv = white ? new[] { 230, 235, 240, 244, 246, 248, 250, 251, 252, 253, 254 } : new[] { 1, 2, 3, 4, 5, 6, 8, 10, 12, 14, 16, 20 };
            int sur = white ? 255 : 0;
            Bitmap bm = new Bitmap(W, H);
            using (Graphics g = Graphics.FromImage(bm))
            using (Font ft = new Font("Segoe UI", Math.Max(14, H / 38f), FontStyle.Bold))
            using (Font fl = new Font("Segoe UI", Math.Max(11, H / 50f), FontStyle.Bold))
            using (Font fi = new Font("Segoe UI", Math.Max(11, H / 54f)))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                float pw = 150f * W / 3840f, ph = 420f * H / 2160f, gap = 20f * W / 3840f;   // scales with the screen
                float bw = lv.Length * pw + (lv.Length + 1) * gap, x0 = (W - bw) / 2f, y0 = H * 0.30f;
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(sur, sur, sur))) g.FillRectangle(sb, x0, y0, bw, ph + 2 * gap);
                for (int i = 0; i < lv.Length; i++)
                {
                    float x = x0 + gap + i * (pw + gap);
                    using (SolidBrush pb = new SolidBrush(Color.FromArgb(lv[i], lv[i], lv[i]))) g.FillRectangle(pb, x, y0 + gap, pw, ph);
                    string t = lv[i].ToString(CultureInfo.InvariantCulture);
                    SizeF ts = g.MeasureString(t, fl);
                    g.DrawString(t, fl, Brushes.White, x + (pw - ts.Width) / 2f, y0 + ph + 2 * gap + H * 0.01f);
                }
                string title = white ? "WHITE CHECK (contrast)" : "BLACK CHECK (brightness)";
                SizeF tt = g.MeasureString(title, ft);
                g.DrawString(title, ft, Brushes.White, (W - tt.Width) / 2f, y0 - tt.Height - H * 0.03f);
                string ins = white
                    ? "Patches 230 to 254 on full white.  Raise contrast until 252 to 254 blend into the white, then lower it until 253 shows again (254 too if you can).\n"
                      + "All the patches should look neutral grey: if the brightest look tinted, lower that colour's high-end gain a step.\n"
                      + "Enter = measure the top end (the meter checks 250 and 254 against white)     S = skip     Esc = stop"
                    : "Patches 1 to 20 on black.  Set brightness so 2 or 3 is just visible and 1 is not.\n"
                      + "8 to 20 should look neutral grey: if they look reddish (or any colour), adjust that colour's low-end offset / bias until they don't.\n"
                      + "Enter = next (the white check)     Esc = stop";
                RectangleF ir = new RectangleF(W * 0.08f, y0 + ph + 2 * gap + H * 0.08f, W * 0.84f, H * 0.25f);
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
                using (SolidBrush ib = new SolidBrush(Color.FromArgb(210, 210, 210)))
                    g.DrawString(ins, fi, ib, ir, sf);
                if (warn != null)
                {
                    // the result, yellow on a dark box with a white outline
                    RectangleF wr = new RectangleF(W * 0.08f, H * 0.84f, W * 0.84f, H * 0.11f);
                    using (SolidBrush wb = new SolidBrush(Color.FromArgb(40, 40, 40))) g.FillRectangle(wb, wr);
                    using (Pen wp = new Pen(Color.White, Math.Max(2, H / 540f))) g.DrawRectangle(wp, wr.X, wr.Y, wr.Width, wr.Height);
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (SolidBrush yb = new SolidBrush(Color.FromArgb(255, 212, 0)))
                        g.DrawString(warn, fi, yb, wr, sf);
                }
            }
            return bm;
        }

        // 1.6: every auto-cal patch is a 10% window on black: full-screen bright
        // patches made the Titan shift colour (it limits laser power) and added
        // room reflections (full white read 30% higher than a window).
        const double CalWindow = 0.10;
        static MScene Full(string id, int r, int g, int b, string note)
        {
            return new MScene { Id = id, Bg = 0, Patch = -1, FR = r, FG = g, FB = b, Note = note, Window = CalWindow };
        }

        // The meter's name from spotread's list of instruments, e.g. "X-Rite i1 DisplayPro, ColorMunki Display".
        static string MeterName(string spotreadExe)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(spotreadExe, "-?");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process pr = Process.Start(psi))
                {
                    string so = "";
                    Thread t = new Thread(() => { try { so = pr.StandardOutput.ReadToEnd(); } catch (Exception) { } });
                    t.IsBackground = true; t.Start();
                    string se = pr.StandardError.ReadToEnd();
                    pr.WaitForExit(15000); t.Join(2000);
                    foreach (string line in (so + "\n" + se).Replace("\r", "").Split('\n'))
                    {
                        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*1\s*=\s*'.*\((.+)\)'");
                        if (m.Success) return m.Groups[1].Value.Trim();
                    }
                }
            }
            catch (Exception) { }
            return "Colorimeter";
        }

        MReading CalRead(string id, int r, int g, int b, double waitSec, int reads)
        {
            MScene sc = Full(id, r, g, b, null);
            Show(sc);
            Thread.Sleep((int)(waitSec * 1000));
            double X = 0, Y = 0, Z = 0; int n = 0; MReading last = null;
            for (int i = 0; i < reads; i++)
            {
                if (cancel) throw new OperationCanceledException();
                MReading m = Reader.Read(60000);
                Row("L", sc, i, -1, m);
                last = m;
                if (m.Ok) { X += m.X; Y += m.Y; Z += m.Z; n++; }
            }
            if (n == 0) return last ?? new MReading { Error = "no reading" };
            return new MReading { Ok = true, X = X / n, Y = Y / n, Z = Z / n };
        }

        // Target XYZ (relative: white = 1 in Y, D65) for an 8-bit RGB, for the chosen gamma and space.
        double[] CalTarget(int r, int g, int b, double yBlack)
        {
            double[,] M = Cal.RgbToXyz(calSpace == 0 ? Cal.P709 : Cal.P2020, Cal.D65);
            double[] v = { r / 255.0, g / 255.0, b / 255.0 };
            double[] lin = new double[3];
            if (calGamma == 2)
            {
                double lb = Math.Max(yBlack, 0), w = 1.0;
                double a = Math.Pow(Math.Pow(w, 1 / 2.4) - Math.Pow(lb, 1 / 2.4), 2.4);
                double bb = Math.Pow(lb, 1 / 2.4) / (Math.Pow(w, 1 / 2.4) - Math.Pow(lb, 1 / 2.4));
                for (int k = 0; k < 3; k++) lin[k] = a * Math.Pow(Math.Max(v[k] + bb, 0), 2.4);
                return Cal.Mul(M, lin);
            }
            double gam = calGamma == 0 ? 2.2 : 2.4;
            for (int k = 0; k < 3; k++) lin[k] = (1 - yBlack) * Math.Pow(v[k], gam);
            double[] X = Cal.Mul(M, lin);
            for (int k = 0; k < 3; k++) X[k] += yBlack * Cal.D65[k];
            return X;
        }

        // The measured colour, adapted from your white to D65 (Bradford, as the
        // LUT's relative mapping does), against the target (1.4.1; 1.4 scaled XYZ,
        // which overstated saturated colours when white is away from D65).
        double CalDe(double[] meas, double[] white, int r, int g, int b, double yBlack)
        {
            double[] Wn = { white[0] / white[1], 1, white[2] / white[1] };
            double[] Xn = { meas[0] / white[1], meas[1] / white[1], meas[2] / white[1] };
            return Cal.DE2000(Cal.Lab(CalTarget(r, g, b, yBlack), Cal.D65), Cal.Lab(Cal.Bradford(Xn, Wn, Cal.D65), Cal.D65));
        }

        static List<CalPatch> CalCheckPatches()
        {
            List<CalPatch> l = new List<CalPatch>();
            for (int i = 0; i <= 10; i++) { int v = (int)Math.Round(i * 25.5); l.Add(new CalPatch { Name = "Grey " + (i * 10) + "%", R = v, G = v, B = v }); }
            l.Add(new CalPatch { Name = "Red", R = 255 }); l.Add(new CalPatch { Name = "Green", G = 255 }); l.Add(new CalPatch { Name = "Blue", B = 255 });
            l.Add(new CalPatch { Name = "Cyan", G = 255, B = 255 }); l.Add(new CalPatch { Name = "Magenta", R = 255, B = 255 }); l.Add(new CalPatch { Name = "Yellow", R = 255, G = 255 });
            l.Add(new CalPatch { Name = "Red 75%", R = 191 }); l.Add(new CalPatch { Name = "Green 75%", G = 191 }); l.Add(new CalPatch { Name = "Blue 75%", B = 191 });
            l.Add(new CalPatch { Name = "Light skin", R = 194, G = 150, B = 130 }); l.Add(new CalPatch { Name = "Dark skin", R = 115, G = 82, B = 68 });
            l.Add(new CalPatch { Name = "Medium skin", R = 166, G = 118, B = 96 });
            l.Add(new CalPatch { Name = "Sky blue", R = 98, G = 122, B = 157 }); l.Add(new CalPatch { Name = "Foliage", R = 87, G = 108, B = 67 });
            l.Add(new CalPatch { Name = "Orange", R = 214, G = 126, B = 44 }); l.Add(new CalPatch { Name = "Purple", R = 94, G = 60, B = 108 });
            return l;
        }

        static string RunTool(string exe, string args, string dir, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = dir;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string so = "";
                Thread t = new Thread(() => { try { so = p.StandardOutput.ReadToEnd(); } catch (Exception) { } });
                t.IsBackground = true; t.Start();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } throw new Exception(Path.GetFileName(exe) + " took too long"); }
                t.Join(5000);
                if (p.ExitCode != 0) throw new Exception(Path.GetFileName(exe) + " failed: " + (se + " " + so).Trim().Replace("\n", " "));
                return so + se;
            }
        }

        // ---- the run ----------------------------------------------------------------
        void CalRun()
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            string file = CalFile();
            csv = new StringBuilder();
            List<string> report = new List<string>();
            Spotread sp = new Spotread(clock);
            reader = sp;
            string pdfPath = "", ti3Text = "";
            bool done = false;
            try
            {
                string ccss = CalCcss();
                if (ccss.Length > 0 && !File.Exists(ccss)) throw new CalStop(CalApp.On
                    ? "Stopped: " + Path.GetFileName(ccss) + " was not found in " + Path.GetDirectoryName(ccss) + "."
                    : "Stopped: " + file + ".ccss is not next to ClipGuardMeter.exe. Put the projector's .ccss there, named " + file + ".ccss.");
                Status("Starting the meter...");
                sp.Exe = Spotread.Find(seedIni);
                if (sp.Exe.Length == 0) throw new CalStop("Stopped: ArgyllCMS spotread.exe was not found.");
                string bin = Path.GetDirectoryName(sp.Exe);
                string colprof = Path.Combine(bin, "colprof.exe"), collink = Path.Combine(bin, "collink.exe");
                string refDir = Path.Combine(Path.GetDirectoryName(bin), "ref");
                string srcIcm = Path.Combine(refDir, calSpace == 0 ? "Rec709.icm" : "Rec2020.icm");
                if (!File.Exists(srcIcm)) srcIcm = Path.Combine(bin, Path.GetFileName(srcIcm));   // 2.66.0: the player keeps Argyll's files together
                if (!File.Exists(colprof) || !File.Exists(collink) || !File.Exists(srcIcm)) throw new CalStop("Stopped: Argyll's colprof, collink or " + Path.GetFileName(srcIcm) + " was not found next to spotread.");
                CalStartMeter(sp, ccss, calRefresh == 1);

                csv.AppendFormat(CultureInfo.InvariantCulture, "# ClipGuard Meter auto-cal {0}: {1}, gamma {2}, {3}; ccss {4}\n", stamp, CalName(), CalGammaNames[calGamma], CalSpaceNames[calSpace], ccss.Length > 0 ? Path.GetFileName(ccss) : "none");
                csv.Append("step,scene,bg,patch,box_level,box_area,box_pos,ramp,rep,t_switch,t_start,t_end,ok,X,Y,Z,error\n");

                // 0. aim (1.7): off the screen, no diffuser; live reading on the white window
                calEsc = false;
                calGo.Reset();
                calWaiting = true;
                double bestAim = 0;
                while (!calGo.WaitOne(0))
                {
                    MReading am = sp.Read(30000);
                    string aimLive = am.Ok ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} nits   highest so far {1:0.0}", am.Y, bestAim = Math.Max(bestAim, am.Y)) : "no reading: " + am.Error;
                    Show(Full("aim", 255, 255, 255, "AIM: meter on the tripod pointing at the screen (no diffuser), on the white window.   " + aimLive + "   Enter = done   Esc = stop"));
                }
                calWaiting = false;
                if (calEsc) throw new OperationCanceledException();

                // 1. dimming off, then white by eye
                if (!CalWait(Full("dimming", 40, 40, 40, "Turn the projector's laser dimming (DBLE / EBL) OFF, then press Enter.   Esc = stop"))) throw new OperationCanceledException();
                CalLevelChecks();   // 2.88.6: black and white level checks, with instructions
                if (calWhite == 0) CalWhitePoint(sp);   // 2.96.0: the perceptual white, with the live balance meter
                else if (calWhite == 2) { }             // 2.97.0: the LUT sets the white; the projector's stays as it is
                else if (!CalWait(Full("white_eye", 255, 255, 255, "Adjust the projector's white balance until this white window looks right to you, then press Enter.   Esc = stop"))) throw new OperationCanceledException();
                Status("Reading your white...");
                MReading w = CalRead("white", 255, 255, 255, 1.0, 3);
                if (!w.Ok) throw new CalStop("Stopped: the meter gave no reading on white (" + w.Error + ").");
                MReading bk = CalRead("black", 0, 0, 0, 1.5, 2);
                double[] W = { w.X, w.Y, w.Z };
                double yBlack = bk.Ok ? Math.Max(0, bk.Y / w.Y) : 0;

                // 2. profiling patches: a 7-level cube and 33 greys
                List<int[]> pts = new List<int[]>();
                HashSet<int> seen = new HashSet<int>();
                Action<int, int, int> add = (r, g, b) => { int k = (r << 16) | (g << 8) | b; if (seen.Add(k)) pts.Add(new int[] { r, g, b }); };
                for (int i = 0; i <= 32; i++) { int v = (int)Math.Round(i * 255.0 / 32); add(v, v, v); }
                for (int r = 0; r <= 6; r++) for (int g = 0; g <= 6; g++) for (int b = 0; b <= 6; b++)
                    add((int)Math.Round(r * 42.5), (int)Math.Round(g * 42.5), (int)Math.Round(b * 42.5));
                List<double[]> xyz = new List<double[]>();
                int failRun = 0;   // 2.88.4: failed patches in a row
                DateTime t0 = DateTime.Now;
                for (int i = 0; i < pts.Count; i++)
                {
                    double per = i > 0 ? (DateTime.Now - t0).TotalSeconds / i : 1.5;
                    Status(string.Format(CultureInfo.InvariantCulture, "Measuring {0}/{1}, about {2:0} min left", i + 1, pts.Count, per * (pts.Count - i) / 60));
                    bool grey = pts[i][0] == pts[i][1] && pts[i][1] == pts[i][2];
                    MReading m = CalRead("p" + i, pts[i][0], pts[i][1], pts[i][2], 0.5, grey ? 3 : 1);   // greys: 3 readings averaged
                    if (!m.Ok) m = CalRead("p" + i + "_again", pts[i][0], pts[i][1], pts[i][2], 0.5, 1);
                    xyz.Add(m.Ok ? new double[] { m.X, m.Y, m.Z } : null);
                    // 2.88.4: the meter stopped answering: stop, never build from part of the readings
                    if (!m.Ok && ++failRun >= 3) throw new CalStop("Stopped at patch " + (i + 1) + " of " + pts.Count + ": the meter stopped answering (" + (m.Error ?? "no reading") + "). Nothing was changed. Unplug the meter, plug it into a back USB port and press C to check it.");
                    if (m.Ok) failRun = 0;
                }
                int nMiss = 0; foreach (double[] x in xyz) if (x == null) nMiss++;
                if (nMiss > pts.Count / 50) throw new CalStop("Stopped: " + nMiss + " of " + pts.Count + " patches had no reading, too many for a good LUT. Nothing was changed. Press C to check the meter, then run it again.");

                // 3. build: .ti3 -> colprof -> collink -> .cube
                Status("Building the LUT (a minute or two)...");
                string work = Path.Combine(appDir, "cal", file + "_" + stamp);
                Directory.CreateDirectory(work);
                StringBuilder ti3 = new StringBuilder();
                ti3.Append("CTI3\n\nDESCRIPTOR \"ClipGuard Meter auto-cal\"\nORIGINATOR \"ClipGuard Meter\"\nDEVICE_CLASS \"DISPLAY\"\nCOLOR_REP \"RGB_XYZ\"\n");
                ti3.AppendFormat(CultureInfo.InvariantCulture, "LUMINANCE_XYZ_CDM2 \"{0:0.######} {1:0.######} {2:0.######}\"\n", W[0], W[1], W[2]);
                ti3.Append("\nNUMBER_OF_FIELDS 7\nBEGIN_DATA_FORMAT\nSAMPLE_ID RGB_R RGB_G RGB_B XYZ_X XYZ_Y XYZ_Z\nEND_DATA_FORMAT\n\n");
                int nOk = 0; foreach (double[] x in xyz) if (x != null) nOk++;
                ti3.Append("NUMBER_OF_SETS " + nOk + "\nBEGIN_DATA\n");
                int id = 1;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (xyz[i] == null) continue;
                    ti3.AppendFormat(CultureInfo.InvariantCulture, "{0} {1:0.####} {2:0.####} {3:0.####} {4:0.######} {5:0.######} {6:0.######}\n",
                        id++, pts[i][0] / 2.55, pts[i][1] / 2.55, pts[i][2] / 2.55, xyz[i][0] / W[1] * 100, xyz[i][1] / W[1] * 100, xyz[i][2] / W[1] * 100);
                }
                ti3.Append("END_DATA\n");
                ti3Text = ti3.ToString();
                File.WriteAllText(Path.Combine(work, "display.ti3"), ti3Text);
                RunTool(colprof, "-qm -aX -r 1.0 -D \"ClipGuard " + CalShortName().Replace("\"", "") + "\" -O display.icc display", work, 15 * 60000);   // -O: Windows Argyll names it .icm otherwise
                string iarg = calGamma == 2 ? "-I B" : (calGamma == 0 ? "-I g:2.2" : "-I g:2.4");
                RunTool(collink, "-qm -G -ir " + iarg + " -3c -r65 \"" + srcIcm + "\" display.icc link.icc", work, 20 * 60000);
                string cubeWork = Path.Combine(work, "link.cube");
                if (!File.Exists(cubeWork)) throw new Exception("collink made no .cube");
                // Black and the darkest steps stay as the projector makes them: the
                // correction fades in from about 2% to 25% (by the brightest channel).
                string cubeFinal = Path.Combine(work, "final.cube");
                Cube.BlendDark(cubeWork, cubeFinal, 0.02, 0.25, 0.80, 1.0);   // 2.88.7: and the gamut edge kept native
                cubeWork = cubeFinal;
                double whiteCost = 0;
                if (calWhite == 2)
                {
                    // 2.97.0: the white set by the LUT. From the measured red, green and blue (full and
                    // their steps), each channel's gain in light that puts the projector's white on the
                    // target, the largest at 1 (a LUT can only take light away), then as code values.
                    Func<int, int, int, double[]> at = (r, g, b) => { for (int i = 0; i < pts.Count; i++) if (pts[i][0] == r && pts[i][1] == g && pts[i][2] == b) return xyz[i]; return null; };
                    double[] K = at(0, 0, 0) ?? new double[3];
                    double[][] P = { at(255, 0, 0), at(0, 255, 0), at(0, 0, 255) };
                    if (P[0] == null || P[1] == null || P[2] == null) throw new CalStop("Stopped: the red, green or blue reading is missing, so the LUT can't set the white. Run it again.");
                    double[,] D = new double[3, 3];
                    for (int c = 0; c < 3; c++) for (int k = 0; k < 3; k++) D[k, c] = P[c][k] - K[k];
                    double[] tw = { PerceptualX / PerceptualY, 1, (1 - PerceptualX - PerceptualY) / PerceptualY };
                    double[] gain = Cal.Mul(Cal.Inv(D), tw);
                    double gmax = Math.Max(gain[0], Math.Max(gain[1], gain[2]));
                    if (!(gmax > 0) || gain[0] <= 0 || gain[1] <= 0 || gain[2] <= 0) throw new CalStop("Stopped: the target white is outside what this projector's red, green and blue can make.");
                    double[] kc = new double[3];
                    for (int c = 0; c < 3; c++)
                    {
                        // the channel's own response (its steps against full), as a gamma
                        double sum = 0; int n = 0;
                        foreach (int v in new[] { 85, 128, 170, 212 })   // the cube steps (round(i x 42.5))
                        {
                            int[] rgb = { c == 0 ? v : 0, c == 1 ? v : 0, c == 2 ? v : 0 };
                            double[] m = at(rgb[0], rgb[1], rgb[2]);
                            if (m == null) continue;
                            double lin = (m[1] - K[1]) / (P[c][1] - K[1]);
                            if (lin > 0.001 && lin < 1) { sum += Math.Log(lin) / Math.Log(v / 255.0); n++; }
                        }
                        double gam = n > 0 ? Math.Max(1.6, Math.Min(3.0, sum / n)) : 2.2;
                        kc[c] = Math.Pow(gain[c] / gmax, 1 / gam);
                    }
                    string cubeW = Path.Combine(work, "white.cube");
                    Cube.ScaleOut(cubeWork, cubeW, kc);
                    cubeWork = cubeW;
                    csv.AppendFormat(CultureInfo.InvariantCulture, "# white set by the LUT: gains R {0:0.0000} G {1:0.0000} B {2:0.0000} (code x {3:0.0000} {4:0.0000} {5:0.0000})\n",
                        gain[0] / gmax, gain[1] / gmax, gain[2] / gmax, kc[0], kc[1], kc[2]);
                }
                Cube cube = Cube.Load(cubeWork);
                if (calWhite == 2)
                {
                    // the white through the LUT, measured: the white the check, the report and the levels use
                    double[] ow = cube.Apply(1, 1, 1);
                    MReading wl = CalRead("white_lut", (int)Math.Round(ow[0] * 255), (int)Math.Round(ow[1] * 255), (int)Math.Round(ow[2] * 255), 1.0, 3);
                    if (wl.Ok && wl.Y > 0)
                    {
                        whiteCost = 1 - wl.Y / W[1];
                        W[0] = wl.X; W[1] = wl.Y; W[2] = wl.Z;
                        yBlack = bk.Ok ? Math.Max(0, bk.Y / wl.Y) : yBlack;
                    }
                }

                // 4. check: before (raw) and after (through the LUT, as the renderer applies it)
                List<CalPatch> chk = CalCheckPatches();
                int chkFail = 0;   // 2.88.4
                for (int i = 0; i < chk.Count; i++)
                {
                    CalPatch p = chk[i];
                    Status(string.Format(CultureInfo.InvariantCulture, "Checking {0}/{1}", i + 1, chk.Count));
                    MReading b0 = CalRead("chk_raw_" + p.Name, p.R, p.G, p.B, 0.5, 3);
                    double[] o = cube.Apply(p.R / 255.0, p.G / 255.0, p.B / 255.0);
                    int ar = (int)Math.Round(Math.Max(0, Math.Min(1, o[0])) * 255), ag = (int)Math.Round(Math.Max(0, Math.Min(1, o[1])) * 255), ab = (int)Math.Round(Math.Max(0, Math.Min(1, o[2])) * 255);
                    MReading a0 = CalRead("chk_lut_" + p.Name, ar, ag, ab, 0.5, 3);
                    if (!b0.Ok && !a0.Ok && ++chkFail >= 3) throw new CalStop("Stopped during the check: the meter stopped answering (" + (a0.Error ?? "no reading") + "). The LUT was built but not installed; nothing was changed. Press C to check the meter, then run it again.");
                    if (b0.Ok || a0.Ok) chkFail = 0;
                    if (b0.Ok) { p.Before = new double[] { b0.X, b0.Y, b0.Z }; p.DeBefore = CalDe(p.Before, W, p.R, p.G, p.B, yBlack); }
                    if (a0.Ok) { p.After = new double[] { a0.X, a0.Y, a0.Z }; p.DeAfter = CalDe(p.After, W, p.R, p.G, p.B, yBlack); }
                }
                double sumB = 0, sumA = 0, maxB = 0, maxA = 0; int nb = 0, na = 0;
                foreach (CalPatch p in chk)
                {
                    if (p.Before != null) { sumB += p.DeBefore; maxB = Math.Max(maxB, p.DeBefore); nb++; }
                    if (p.After != null) { sumA += p.DeAfter; maxA = Math.Max(maxA, p.DeAfter); na++; }
                }
                double avgB = sumB / Math.Max(1, nb), avgA = sumA / Math.Max(1, na);
                if (na < chk.Count * 3 / 4) throw new CalStop("Stopped: only " + na + " of " + chk.Count + " check patches were read, so the LUT can't be checked. It was not installed; nothing was changed. Press C to check the meter, then run it again.");   // 2.88.4

                // 5. install for the renderer: <file>.cube + <file>.txt next to clipguard.ini
                // The renderer runs from ClipGuard's working copy of Unplugged
                // (%LOCALAPPDATA%\ClipGuard\Unplugged\filters, next to its clipguard.ini):
                // the LUT goes there. A copy stays next to ClipGuardMeter.exe.
                string iniDir = Path.GetDirectoryName(seedIni);
                string live = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipGuard", "Unplugged", "filters");
                if (CalApp.On) { Directory.CreateDirectory(CalApp.OutDir); iniDir = live = CalApp.OutDir; }   // 2.66.0: the player's Calibration folder
                if (!Directory.Exists(live)) throw new CalStop("Stopped: ClipGuard's renderer folder was not found (" + live + "). Start ClipGuard once, then run auto-cal again.");
                File.Copy(cubeWork, Path.Combine(live, file + ".cube"), true);
                File.Copy(cubeWork, Path.Combine(iniDir, file + ".cube"), true);
                string desc = string.Format(CultureInfo.InvariantCulture, "{0}, {1} {2}, dE {3:0.0}, {4}", CalShortName(), CalSpaceNames[calSpace], CalGammaNames[calGamma], avgA, DateTime.Now.ToString("d MMM", CultureInfo.InvariantCulture));
                // 2.91.0: the measured white and black too, so the player can set its display levels from the LUT
                string levels = string.Format(CultureInfo.InvariantCulture, "white_nits={0:0.0}\nblack_nits={1:0.0000}\n", W[1], yBlack * W[1]);
                File.WriteAllText(Path.Combine(live, file + ".txt"), desc + "\n" + levels);
                File.WriteAllText(Path.Combine(iniDir, file + ".txt"), desc + "\n" + levels);

                // 6. report
                pdfPath = Path.Combine(iniDir, "clipguard_" + file + "_" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".pdf");
                CalReport(pdfPath, chk, W, yBlack, avgB, maxB, avgA, maxA, stamp, ccss.Length > 0 ? Path.GetFileName(ccss) : "none (no spectral correction)", MeterName(sp.Exe));
                if (CalApp.On) report.Add("LUT saved: " + file + ".cube (" + desc + "). Choose it in the player: Picture > Calibration LUT > LUT.");
                else report.Add("LUT saved: " + file + ".cube (" + desc + "). ClipGuard loads it within a few seconds when the menu's LUT row is set to " + (file == "titan" ? "Titan" : "Valerion") + ".");
                report.Add(string.Format(CultureInfo.InvariantCulture, "Average error: {0:0.0} before, {1:0.0} after. Worst: {2:0.0} before, {3:0.0} after.", avgB, avgA, maxB, maxA));
                if (calWhite == 2) report.Add(string.Format(CultureInfo.InvariantCulture, "White set by the LUT to x {0:0.0000} y {1:0.0000}: {2:0.0} nits ({3:0}% of the projector's own white).", PerceptualX, PerceptualY, W[1], (1 - whiteCost) * 100));
                report.Add("Report: " + pdfPath);
                report.Add("Turn the laser dimming back on for watching.");
                done = true;
            }
            catch (CalStop ex) { report.Add(ex.Message); }
            catch (OperationCanceledException) { report.Add("Auto-cal stopped. Nothing was changed."); }
            catch (Exception ex) { report.Add("Auto-cal failed: " + ex.Message + ". Nothing was changed."); }
            finally
            {
                calWaiting = false;
                sp.Dispose();
                Show(new MScene { Id = "idle", Bg = 16, Patch = -1 });
            }

            if (CalApp.On) report.Add("Esc closes calibration and returns to the player.");
            string summary = string.Join("\n", report.ToArray());

            // 2.97.1: from the player, a stopped or failed run goes straight back to the setup
            if (CalApp.On && !done) { calSetup = true; calRow = 0; SetIdle(summary + "\n\n" + CalSetupText()); return; }
            SetIdle("Auto-cal finished.\n\n" + summary + "\n\nL = auto-cal again   Esc = quit");
        }

        // ---- PDF report -------------------------------------------------------------
        // Colour-blind friendly: blue / yellow / red with white outlines on dark panels.
        static readonly Color CBlue = Color.FromArgb(40, 110, 230), CYellow = Color.FromArgb(240, 200, 30), CRed = Color.FromArgb(220, 40, 40);
        static readonly Color CPanel = Color.FromArgb(40, 40, 46), CGrid = Color.FromArgb(90, 90, 100);
        static Color DeColor(double de) { return de < 1 ? CBlue : (de < 3 ? CYellow : CRed); }

        void CalReport(string path, List<CalPatch> chk, double[] W, double yBlack, double avgB, double maxB, double avgA, double maxA,
                       string stamp, string ccss, string meterInfo)
        {
            // One page. Dark cards on a white page so the blue / yellow / red marks
            // keep their white outlines (colour-blind friendly).
            Pdf p = new Pdf();
            Color ink = Color.FromArgb(30, 30, 34), soft = Color.FromArgb(110, 110, 120), card = Color.FromArgb(34, 36, 42);
            Color cardText = Color.FromArgb(235, 235, 240), cardSoft = Color.FromArgb(150, 152, 165);
            double L = 44, R = Pdf.PW - 44, Wd = R - L;

            // header
            p.Text(L, 58, 24, "ClipGuard Calibration", true, ink);
            string date = DateTime.Now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            p.Text(R - 6.2 * date.Length, 58, 11, date, false, soft);
            p.Text(L, 80, 11, CalName() + "   |   " + CalSpaceNames[calSpace] + "   |   Gamma " + CalGammaNames[calGamma] + (calWhite == 0 ? string.Format(CultureInfo.InvariantCulture, "   |   White: perceptual, x {0:0.0000} y {1:0.0000}", PerceptualX, PerceptualY)
                : calWhite == 2 ? string.Format(CultureInfo.InvariantCulture, "   |   White: perceptual, x {0:0.0000} y {1:0.0000}, set by the LUT", PerceptualX, PerceptualY)
                : "   |   White: yours, set by eye"), false, ink);
            p.Text(L, 95, 9, meterInfo + "   |   " + ccss + "   |   " + CalModeNames[calRefresh].Split(' ')[0] + " mode", false, soft);
            p.Line(L, 106, R, 106, Color.FromArgb(215, 215, 222), 1, false);

            // result card
            double y = 120;
            p.Rect(L, y, Wd, 96, card, null, 0);
            p.Rect(L + 18, y + 20, 56, 56, DeColor(avgA), Color.White, 2);
            p.Text(L + 90, y + 34, 10, "AVERAGE COLOUR ERROR", true, cardSoft);
            p.Text(L + 90, y + 66, 30, avgA.ToString("0.0", CultureInfo.InvariantCulture), true, cardText);
            p.Text(L + 150, y + 66, 12, "was " + avgB.ToString("0.0", CultureInfo.InvariantCulture), false, cardSoft);
            p.Rect(L + 270, y + 30, 36, 36, DeColor(maxA), Color.White, 2);
            p.Text(L + 320, y + 42, 10, "WORST PATCH", true, cardSoft);
            p.Text(L + 320, y + 66, 20, maxA.ToString("0.0", CultureInfo.InvariantCulture), true, cardText);
            p.Text(L + 362, y + 66, 11, "was " + maxB.ToString("0.0", CultureInfo.InvariantCulture), false, cardSoft);
            p.Text(L + 18, y + 89, 8, "dE 2000:  blue under 1 = not visible     yellow 1-3 = hard to see     red over 3 = visible", false, cardSoft);

            // greyscale card
            y = 230;
            List<CalPatch> greys = chk.FindAll(c => c.Name.StartsWith("Grey"));
            double ch = 190;
            p.Rect(L, y, Wd, ch, card, null, 0);
            p.Text(L + 18, y + 22, 12, "Greyscale", true, cardText);
            p.Text(L + 92, y + 22, 9, "error per step   (outline = before, coloured = after)", false, cardSoft);
            // Scale to 10 with the 'not visible' and 'hard to see' zones shaded, so the
            // numbers read in context: a factory greyscale of 2-4 is normal, not a fault.
            double gx = L + 40, gw = Wd - 120, gTop = y + 40, gH = 110, maxDe = 10;
            p.Rect(gx, gTop + gH - gH * 1 / maxDe, gw, gH * 1 / maxDe, Color.FromArgb(38, 52, 78), null, 0);
            p.Rect(gx, gTop + gH - gH * 3 / maxDe, gw, gH * 2 / maxDe, Color.FromArgb(56, 54, 40), null, 0);
            p.Text(gx + gw + 8, gTop + gH - gH * 0.5 / maxDe + 3, 7, "not visible", false, cardSoft);
            p.Text(gx + gw + 8, gTop + gH - gH * 2 / maxDe + 3, 7, "hard to see", false, cardSoft);
            p.Text(gx + gw + 8, gTop + gH - gH * 6.5 / maxDe + 3, 7, "visible", false, cardSoft);
            foreach (int i in new int[] { 0, 1, 3, 5, 10 })
            {
                double gy = gTop + gH - gH * i / maxDe;
                p.Line(gx, gy, gx + gw, gy, Color.FromArgb(70, 72, 80), 0.5, false);
                p.Text(gx - 14, gy + 3, 7, i.ToString(CultureInfo.InvariantCulture), false, cardSoft);
            }
            double slot = gw / greys.Count;
            for (int i = 0; i < greys.Count; i++)
            {
                CalPatch g = greys[i];
                double x0 = gx + i * slot + slot * 0.18;
                if (g.Before != null) { double h = gH * Math.Min(g.DeBefore, maxDe) / maxDe; p.Rect(x0, gTop + gH - h, slot * 0.26, Math.Max(h, 0.8), null, Color.FromArgb(140, 142, 152), 0.8); }
                if (g.After != null) { double h = gH * Math.Min(g.DeAfter, maxDe) / maxDe; p.Rect(x0 + slot * 0.32, gTop + gH - h, slot * 0.3, Math.Max(h, 1.5), DeColor(g.DeAfter), Color.White, 0.8); }
                p.Text(gx + i * slot + slot * 0.2, gTop + gH + 12, 7, (i * 10) + "%", false, cardSoft);
            }
            // gamma and balance, one line each
            double gsum = 0; int gn = 0; double bal = 0;
            double[,] Mi = Cal.Inv(Cal.RgbToXyz(calSpace == 0 ? Cal.P709 : Cal.P2020, new double[] { W[0] / W[1], 1, W[2] / W[1] }));
            for (int i = 1; i <= 9; i++)
            {
                if (greys[i].After == null) continue;
                double v = i / 10.0, yr = greys[i].After[1] / W[1];
                if (yr > 0) { gsum += Math.Log(yr) / Math.Log(v); gn++; }
                double[] rgb = Cal.Mul(Mi, new double[] { greys[i].After[0] / W[1], greys[i].After[1] / W[1], greys[i].After[2] / W[1] });
                double avg = (rgb[0] + rgb[1] + rgb[2]) / 3;
                for (int k = 0; k < 3; k++) bal = Math.Max(bal, Math.Abs(rgb[k] / avg - 1) * 100);
            }
            p.Text(L + 18, y + ch - 12, 9, string.Format(CultureInfo.InvariantCulture, "Gamma after: {0:0.00} average (target {1})        Grey balance after: within {2:0.0}% of neutral",
                gn > 0 ? gsum / gn : 0, CalGammaNames[calGamma], bal), false, cardText);

            // colours card
            y = 434;
            List<CalPatch> cols = chk.FindAll(c => !c.Name.StartsWith("Grey"));
            p.Rect(L, y, Wd, 196, card, null, 0);
            p.Text(L + 18, y + 22, 12, "Colours", true, cardText);
            p.Text(L + 78, y + 22, 9, "error after   (was before)", false, cardSoft);
            int perRow = 8;
            double cw = (Wd - 36) / perRow;
            for (int i = 0; i < cols.Count; i++)
            {
                CalPatch c = cols[i];
                double x0 = L + 18 + (i % perRow) * cw, y0 = y + 38 + (i / perRow) * 78;
                p.Rect(x0 + 4, y0, cw - 14, 26, Color.FromArgb(c.R, c.G, c.B), Color.White, 1);
                string nm = c.Name.Length > 12 ? c.Name.Substring(0, 12) : c.Name;
                p.Text(x0 + 4, y0 + 38, 7.5, nm, false, cardSoft);
                if (c.After != null)
                {
                    p.Rect(x0 + 4, y0 + 45, 11, 11, DeColor(c.DeAfter), Color.White, 0.8);
                    p.Text(x0 + 19, y0 + 54, 10, c.DeAfter.ToString("0.0", CultureInfo.InvariantCulture), true, cardText);
                }
                if (c.Before != null) p.Text(x0 + 42, y0 + 54, 8, "(" + c.DeBefore.ToString("0.0", CultureInfo.InvariantCulture) + ")", false, cardSoft);
            }

            // footer: your white and the LUT
            y = 648;
            double[] xy = Cal.Xy(W);
            p.Text(L, y, 11, "Your white", true, ink);
            p.Text(L, y + 16, 9, string.Format(CultureInfo.InvariantCulture, "Peak white {0:0.0} nits (10% window)   |   black {1:0.000} cd/m2   |   contrast {2:0}:1   |   x {3:0.0000}  y {4:0.0000}   (about {5:0} K)",
                W[1], yBlack * W[1], yBlack > 0 ? 1 / yBlack : 0, xy[0], xy[1], Math.Round(Cal.Cct(W) / 50) * 50), false, ink);
            p.Text(L, y + 40, 11, "LUT", true, ink);
            p.Text(L, y + 56, 9, CalFile() + ".cube   |   65 x 65 x 65   |   ArgyllCMS   |   edge colours and near-black left native   |   loaded by ClipGuard for " + CalName(), false, ink);
            p.Line(L, 742, R, 742, Color.FromArgb(215, 215, 222), 1, false);
            p.Text(L, 756, 8, "Errors are against the target with your white as white, so white itself reads 0. Before = without the LUT, after = through it.   Run " + stamp, false, soft);
            p.Save(path);
        }
    }
}
