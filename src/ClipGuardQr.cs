// ClipGuard: a small QR code encoder (byte mode, error correction L), for the
// Media Player's Plex sign-in. Follows the QR Code Model 2 standard (ISO/IEC
// 18004); structure after Project Nayuki's reference implementation.
//
// C# 5 (the packages are built with the .NET Framework 4 compiler).

using System;
using System.Collections.Generic;
using System.Text;

namespace ClipGuard
{
    static class Qr
    {
        static readonly int[] EccPerBlockL = { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 };
        static readonly int[] BlocksL = { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 };

        // The modules, [y, x], true = dark. mask: -1 picks the best, 0-7 forces one (for testing).
        public static bool[,] Encode(string text, int mask)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int ver = 1;
            for (; ver <= 40; ver++)
            {
                int bits = 4 + (ver <= 9 ? 8 : 16) + data.Length * 8;
                if (bits <= DataCodewords(ver) * 8) break;
            }
            if (ver > 40) throw new ArgumentException("Text too long for a QR code");

            // Data bits: byte mode, length, bytes, terminator, padding.
            List<bool> bb = new List<bool>();
            Append(bb, 4, 4);
            Append(bb, data.Length, ver <= 9 ? 8 : 16);
            foreach (byte b in data) Append(bb, b, 8);
            int cap = DataCodewords(ver) * 8;
            Append(bb, 0, Math.Min(4, cap - bb.Count));
            Append(bb, 0, (8 - bb.Count % 8) % 8);
            for (int pad = 0xEC; bb.Count < cap; pad ^= 0xEC ^ 0x11) Append(bb, pad, 8);
            byte[] cw = new byte[bb.Count / 8];
            for (int i = 0; i < bb.Count; i++) if (bb[i]) cw[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            Sym q = new Sym(ver);
            q.DrawFunction();
            q.DrawCodewords(AddEcc(cw, ver));
            if (mask < 0)
            {
                int best = 0, bestPen = int.MaxValue;
                for (int m = 0; m < 8; m++)
                {
                    q.ApplyMask(m);
                    q.DrawFormat(m);
                    int p = q.Penalty();
                    if (p < bestPen) { bestPen = p; best = m; }
                    q.ApplyMask(m);   // undo
                }
                mask = best;
            }
            q.ApplyMask(mask);
            q.DrawFormat(mask);
            return q.M;
        }

        static void Append(List<bool> bb, int val, int len)
        {
            for (int i = len - 1; i >= 0; i--) bb.Add(((val >> i) & 1) != 0);
        }

        static int RawModules(int ver)
        {
            int r = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int na = ver / 7 + 2;
                r -= (25 * na - 10) * na - 55;
                if (ver >= 7) r -= 36;
            }
            return r;
        }

        static int DataCodewords(int ver) { return RawModules(ver) / 8 - EccPerBlockL[ver] * BlocksL[ver]; }

        static byte[] AddEcc(byte[] data, int ver)
        {
            int nb = BlocksL[ver], el = EccPerBlockL[ver];
            int raw = RawModules(ver) / 8;
            int nShort = nb - raw % nb, shortLen = raw / nb;
            byte[] div = RsDivisor(el);
            List<byte[]> blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < nb; i++)
            {
                int dl = shortLen - el + (i < nShort ? 0 : 1);
                byte[] dat = new byte[dl];
                Array.Copy(data, k, dat, 0, dl);
                k += dl;
                byte[] ecc = RsRemainder(dat, div);
                byte[] blk = new byte[shortLen + 1];
                int p = 0;
                foreach (byte b in dat) blk[p++] = b;
                if (i < nShort) p++;   // dummy, skipped when interleaving
                foreach (byte b in ecc) blk[p++] = b;
                blocks.Add(blk);
            }
            List<byte> res = new List<byte>();
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortLen - el || j >= nShort) res.Add(blocks[j][i]);
            return res.ToArray();
        }

        static byte[] RsDivisor(int degree)
        {
            byte[] r = new byte[degree];
            r[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < r.Length; j++)
                {
                    r[j] = (byte)Mul(r[j], root);
                    if (j + 1 < r.Length) r[j] ^= r[j + 1];
                }
                root = Mul(root, 0x02);
            }
            return r;
        }

        static byte[] RsRemainder(byte[] data, byte[] div)
        {
            byte[] r = new byte[div.Length];
            foreach (byte b in data)
            {
                int f = b ^ r[0];
                Array.Copy(r, 1, r, 0, r.Length - 1);
                r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= (byte)Mul(div[i], f);
            }
            return r;
        }

        static int Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xFF;
        }

        sealed class Sym
        {
            public readonly int Ver, Size;
            public readonly bool[,] M;
            readonly bool[,] F;   // function modules

            public Sym(int ver)
            {
                Ver = ver;
                Size = ver * 4 + 17;
                M = new bool[Size, Size];
                F = new bool[Size, Size];
            }

            void Set(int x, int y, bool dark) { M[y, x] = dark; F[y, x] = true; }

            public void DrawFunction()
            {
                for (int i = 0; i < Size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
                Finder(3, 3); Finder(Size - 4, 3); Finder(3, Size - 4);
                int[] ap = AlignPos();
                int n = ap.Length;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                                Set(ap[i] + dx, ap[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                DrawFormat(0);
                if (Ver >= 7)
                {
                    int rem = Ver;
                    for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                    int bits = Ver << 12 | rem;
                    for (int i = 0; i < 18; i++)
                    {
                        bool bit = ((bits >> i) & 1) != 0;
                        int a = Size - 11 + i % 3, b = i / 3;
                        Set(a, b, bit);
                        Set(b, a, bit);
                    }
                }
            }

            void Finder(int x, int y)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int d = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                        if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) Set(xx, yy, d != 2 && d != 4);
                    }
            }

            int[] AlignPos()
            {
                if (Ver == 1) return new int[0];
                int na = Ver / 7 + 2;
                int step = Ver == 32 ? 26 : (Ver * 4 + na * 2 + 1) / (na * 2 - 2) * 2;
                int[] r = new int[na];
                r[0] = 6;
                for (int i = na - 1, pos = Size - 7; i >= 1; i--, pos -= step) r[i] = pos;
                return r;
            }

            public void DrawFormat(int mask)
            {
                int data = 1 << 3 | mask;   // L = 01
                int rem = data;
                for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
                int bits = (data << 10 | rem) ^ 0x5412;
                for (int i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
                Set(8, 7, Bit(bits, 6));
                Set(8, 8, Bit(bits, 7));
                Set(7, 8, Bit(bits, 8));
                for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));
                for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(bits, i));
                for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(bits, i));
                Set(8, Size - 8, true);
            }

            static bool Bit(int x, int i) { return ((x >> i) & 1) != 0; }

            public void DrawCodewords(byte[] data)
            {
                int i = 0;
                for (int right = Size - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right = 5;
                    for (int vert = 0; vert < Size; vert++)
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j;
                            bool up = ((right + 1) & 2) == 0;
                            int y = up ? Size - 1 - vert : vert;
                            if (!F[y, x] && i < data.Length * 8)
                            {
                                M[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                                i++;
                            }
                        }
                }
            }

            public void ApplyMask(int m)
            {
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        bool inv;
                        switch (m)
                        {
                            case 0: inv = (x + y) % 2 == 0; break;
                            case 1: inv = y % 2 == 0; break;
                            case 2: inv = x % 3 == 0; break;
                            case 3: inv = (x + y) % 3 == 0; break;
                            case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                            case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                            case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                            default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                        }
                        if (inv && !F[y, x]) M[y, x] = !M[y, x];
                    }
            }

            // The standard's penalty score (lower reads better).
            public int Penalty()
            {
                int p = 0;
                for (int pass = 0; pass < 2; pass++)
                    for (int a = 0; a < Size; a++)
                    {
                        int run = 0;
                        bool last = false;
                        for (int b = 0; b < Size; b++)
                        {
                            bool c = pass == 0 ? M[a, b] : M[b, a];
                            if (b > 0 && c == last) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                            else run = 1;
                            last = c;
                        }
                    }
                for (int y = 0; y < Size - 1; y++)
                    for (int x = 0; x < Size - 1; x++)
                    {
                        bool c = M[y, x];
                        if (c == M[y, x + 1] && c == M[y + 1, x] && c == M[y + 1, x + 1]) p += 3;
                    }
                bool[] pat = { true, false, true, true, true, false, true };
                for (int pass = 0; pass < 2; pass++)
                    for (int a = 0; a < Size; a++)
                        for (int b = 0; b + 7 <= Size; b++)
                        {
                            bool hit = true;
                            for (int k = 0; k < 7 && hit; k++) hit = (pass == 0 ? M[a, b + k] : M[b + k, a]) == pat[k];
                            if (!hit) continue;
                            bool before = true, after = true;
                            for (int k = 1; k <= 4; k++)
                            {
                                int i1 = b - k, i2 = b + 6 + k;
                                if (i1 >= 0 && (pass == 0 ? M[a, i1] : M[i1, a])) before = false;
                                if (i2 < Size && (pass == 0 ? M[a, i2] : M[i2, a])) after = false;
                            }
                            if (before || after) p += 40;
                        }
                int dark = 0;
                foreach (bool c in M) if (c) dark++;
                int total = Size * Size;
                int k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
                p += Math.Max(0, k2) * 10;
                return p;
            }
        }
    }
}
