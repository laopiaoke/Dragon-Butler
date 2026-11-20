using System;
using System.Collections.Generic;
using System.Text;

namespace DragonButler;

/// <summary>
/// 二维码编码器（v1.31，字节模式，纠错等级 L，版本 1–7）。
///
/// 为什么要自己写：米游社扫码登录要显示一张二维码，而本项目的底线是零第三方依赖
/// （NuGet 不引、脚本库不引）。QR 编码是公开的、确定的算法，写一遍就是一劳永逸。
///
/// 只做了这件事需要的那一档：字节模式（URL 是 ASCII）、纠错 L（屏幕上的码不会脏到需要 H）、
/// 版本 1–7（v7 装得下 154 字节，够放那条登录 URL 还有余）。
/// 没有做：数字/字母数字模式、汉字模式、结构化追加、版本 8 以上。用不到就不写。
/// </summary>
public static class QrCode
{
    /// <summary>一个版本的参数：总码字数、每块纠错码字数、各块数据码字数。</summary>
    private sealed record Version(int Total, int EcPerBlock, int[] BlockData);

    private static readonly Version?[] Versions =
    {
        null,                                   // 占位：版本号从 1 开始
        new(26, 7, new[] { 19 }),               // 1
        new(44, 10, new[] { 34 }),              // 2
        new(70, 15, new[] { 55 }),              // 3
        new(100, 20, new[] { 80 }),             // 4
        new(134, 26, new[] { 108 }),            // 5
        new(172, 18, new[] { 68, 68 }),         // 6
        new(196, 20, new[] { 78, 78 }),         // 7
    };

    /// <summary>各版本的校正图形中心坐标（版本 1 没有校正图形）。</summary>
    private static readonly int[][] Align =
    {
        Array.Empty<int>(),
        Array.Empty<int>(),
        new[] { 6, 18 },
        new[] { 6, 22 },
        new[] { 6, 26 },
        new[] { 6, 30 },
        new[] { 6, 34 },
        new[] { 6, 22, 38 },
    };

    /// <summary>
    /// 把一段 UTF-8 文本编成模块矩阵。true = 黑。
    /// 数据太长（超过版本 7 的容量）时返回 null——调用方该做的是缩短内容，而不是硬塞。
    /// </summary>
    public static bool[,]? Encode(string text)
    {
        var (m, _) = DebugEncode(text);
        return m;
    }

    /// <summary>给探针用的版本：把挑中的掩码也带出来，断言时能说清「差在哪」。</summary>
    public static (bool[,]? Matrix, int Mask) DebugEncode(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text ?? "");

        int version = 0;
        for (int v = 1; v < Versions.Length; v++)
        {
            var cand = Versions[v]!;
            int cap = 0;
            foreach (int d in cand.BlockData) cap += d;
            // 模式 4 位 + 字符计数 8 位（版本 1–9）= 12 位 = 1.5 字节
            if (payload.Length + 2 <= cap) { version = v; break; }
        }
        if (version == 0) return (null, -1);

        var spec = Versions[version]!;
        int dataCapacity = 0;
        foreach (int d in spec.BlockData) dataCapacity += d;

        // ---- 1) 数据位流 ----
        var bits = new BitBuffer();
        bits.Put(0b0100, 4);                 // 字节模式
        bits.Put(payload.Length, 8);         // 版本 1–9 的计数是 8 位
        foreach (byte b in payload) bits.Put(b, 8);

        int capacityBits = dataCapacity * 8;
        int terminator = Math.Min(4, capacityBits - bits.Length);
        bits.Put(0, terminator);
        while (bits.Length % 8 != 0) bits.Put(0, 1);

        byte[] data = bits.ToBytes();
        var padded = new List<byte>(data);
        byte[] padBytes = { 0xEC, 0x11 };
        int pi = 0;
        while (padded.Count < dataCapacity) padded.Add(padBytes[pi++ % 2]);
        data = padded.ToArray();

        // ---- 2) 分块 + 纠错 ----
        var blocks = new List<byte[]>();
        var eccs = new List<byte[]>();
        int offset = 0;
        foreach (int blockData in spec.BlockData)
        {
            var chunk = new byte[blockData];
            Array.Copy(data, offset, chunk, 0, blockData);
            offset += blockData;
            blocks.Add(chunk);
            eccs.Add(ReedSolomon(chunk, spec.EcPerBlock));
        }

        // ---- 3) 交织 ----
        var final = new List<byte>(spec.Total);
        int maxData = 0;
        foreach (var b in blocks) maxData = Math.Max(maxData, b.Length);
        for (int i = 0; i < maxData; i++)
            foreach (var b in blocks)
                if (i < b.Length) final.Add(b[i]);
        for (int i = 0; i < spec.EcPerBlock; i++)
            foreach (var e in eccs) final.Add(e[i]);

        // ---- 4) 摆进矩阵 ----
        int size = 17 + 4 * version;
        var matrix = new bool[size, size];
        var reserved = new bool[size, size];

        DrawFunctionPatterns(matrix, reserved, version, size);

        int bitIndex = 0;
        int totalBits = final.Count * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;                  // 第 6 列是竖直定时图形，跳过去
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (reserved[y, x]) continue;
                    bool dark = false;
                    if (bitIndex < totalBits)
                    {
                        int byteIdx = bitIndex >> 3;
                        int bit = 7 - (bitIndex & 7);
                        dark = ((final[byteIdx] >> bit) & 1) != 0;
                    }
                    matrix[y, x] = dark;
                    bitIndex++;
                }
            }
        }

        // ---- 5) 挑一个掩码 ----
        int bestMask = 0;
        int bestScore = int.MaxValue;
        bool[,]? best = null;
        for (int mask = 0; mask < 8; mask++)
        {
            var trial = (bool[,])matrix.Clone();
            // 掩码只盖数据区。定位图形/定时图形/校正图形/格式信息都是「功能模块」，
            // 它们必须保持原样——把它们一起翻黑白，扫出来就是一坨噪声，而且不会报错，
            // 只会「扫不出来」。这是自己写 QR 最容易栽的一处。
            ApplyMask(trial, reserved, mask, size);
            DrawFormatInfo(trial, size, mask);
            int score = Penalty(trial, size);
            if (score < bestScore) { bestScore = score; bestMask = mask; best = trial; }
        }

        return (best, bestMask);
    }

    // ===== 图形 =====

    private static void DrawFunctionPatterns(bool[,] m, bool[,] r, int version, int size)
    {
        // 定位图形（三个角上的「回」字）+ 它们的一圈空白分隔
        Finder(m, r, 0, 0, size);
        Finder(m, r, 0, size - 7, size);
        Finder(m, r, size - 7, 0, size);

        // 定时图形：第 6 行 / 第 6 列的黑白相间
        for (int i = 8; i < size - 8; i++)
        {
            bool dark = i % 2 == 0;
            Set(m, r, i, 6, dark);
            Set(m, r, 6, i, dark);
        }

        // 校正图形
        int[] centers = Align[version];
        foreach (int cy in centers)
            foreach (int cx in centers)
            {
                if ((cy <= 8 && cx <= 8) || (cy <= 8 && cx >= size - 9) || (cy >= size - 9 && cx <= 8))
                    continue;                       // 和定位图形重叠的位置不画
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        bool dark = Math.Max(Math.Abs(dy), Math.Abs(dx)) != 1;
                        Set(m, r, cy + dy, cx + dx, dark);
                    }
            }

        // 固定黑点 + 预留格式信息的位置
        // 固定黑点在 (行 size-8, 列 8)，它不属于任何一段格式信息，单独画。
        Set(m, r, size - 8, 8, true);
        for (int i = 0; i < 9; i++)
        {
            if (i != 6) { Reserve(r, 8, i); Reserve(r, i, 8); }
        }
        for (int i = 0; i < 8; i++)
        {
            Reserve(r, size - 1 - i, 8);
            Reserve(r, 8, size - 1 - i);
        }

        // 版本 7 起要额外写一块版本信息
        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int value = (version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((value >> i) & 1) != 0;
                int a = size - 11 + i % 3;
                int b = i / 3;
                Set(m, r, b, a, bit);
                Set(m, r, a, b, bit);
            }
        }
    }

    private static void Finder(bool[,] m, bool[,] r, int top, int left, int size)
    {
        for (int dy = -1; dy <= 7; dy++)
            for (int dx = -1; dx <= 7; dx++)
            {
                int y = top + dy, x = left + dx;
                if (y < 0 || y >= size || x < 0 || x >= size) continue;
                bool inside = dy >= 0 && dy <= 6 && dx >= 0 && dx <= 6;
                bool dark = inside
                    && (dy == 0 || dy == 6 || dx == 0 || dx == 6
                        || (dy >= 2 && dy <= 4 && dx >= 2 && dx <= 4));
                Set(m, r, y, x, dark);
            }
    }

    private static void Set(bool[,] m, bool[,] r, int y, int x, bool dark)
    {
        if (y < 0 || x < 0 || y >= m.GetLength(0) || x >= m.GetLength(1)) return;
        m[y, x] = dark;
        r[y, x] = true;
    }

    private static void Reserve(bool[,] r, int y, int x) => r[y, x] = true;

    private static void DrawFormatInfo(bool[,] m, int size, int mask)
    {
        // 纠错等级 L = 01；15 位 BCH，最后异或 0x5412
        int data = (0b01 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        // 15 位写两遍，两遍都是完整的 15 位，位置是标准表规定死的：
        //
        //   竖着那遍（第 8 列）：i = 0..5 → 行 i；i = 6,7 → 行 i+1（跳过第 6 行的定时图形）；
        //                        i = 8..14 → 行 size-15+i
        //   横着那遍（第 8 行）：i = 0..7 → 列 size-1-i；i = 8 → 列 7；i = 9..14 → 列 14-i
        //
        // 这里最容易犯的错是把两遍写成「各写一半」，或者行和列写反。后果不是崩溃，
        // 而是读码器读不到完整格式信息——**不报错、直接扫不出来**，比崩溃难查得多。
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;

            int col = 8;
            int row = i < 6 ? i : (i < 8 ? i + 1 : size - 15 + i);
            m[row, col] = bit;
        }
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;

            int row = 8;
            int col = i < 8 ? size - 1 - i : (i == 8 ? 7 : 14 - i);
            m[row, col] = bit;
        }

        m[size - 8, 8] = true;   // 固定黑点，写完格式信息再确认一次
    }

    private static void ApplyMask(bool[,] m, bool[,] r, int mask, int size)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (r[y, x]) continue;                // 功能模块不参与掩码
                bool flip = mask switch
                {
                    0 => (y + x) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (y + x) % 3 == 0,
                    4 => (y / 2 + x / 3) % 2 == 0,
                    5 => (y * x) % 2 + (y * x) % 3 == 0,
                    6 => ((y * x) % 2 + (y * x) % 3) % 2 == 0,
                    _ => ((y + x) % 2 + (y * x) % 3) % 2 == 0,
                };
                if (flip) m[y, x] = !m[y, x];
            }
    }

    /// <summary>四条标准罚分。挑掩码就是挑罚分最低的那个。</summary>
    private static int Penalty(bool[,] m, int size)
    {
        int score = 0;

        // 规则 1：同色连续 5 个以上
        for (int y = 0; y < size; y++)
        {
            int run = 1;
            for (int x = 1; x < size; x++)
            {
                if (m[y, x] == m[y, x - 1]) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                else run = 1;
            }
        }
        for (int x = 0; x < size; x++)
        {
            int run = 1;
            for (int y = 1; y < size; y++)
            {
                if (m[y, x] == m[y - 1, x]) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                else run = 1;
            }
        }

        // 规则 2：2×2 同色块
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                bool c = m[y, x];
                if (m[y, x + 1] == c && m[y + 1, x] == c && m[y + 1, x + 1] == c) score += 3;
            }

        // 规则 3：长得像定位图形的 1:1:3:1:1 序列
        int[] pattern = { 1, 0, 1, 1, 1, 0, 1 };
        for (int y = 0; y < size; y++)
            for (int x = 0; x <= size - 7; x++)
                if (MatchRun(m, y, x, pattern, true)) score += 40;
        for (int x = 0; x < size; x++)
            for (int y = 0; y <= size - 7; y++)
                if (MatchRun(m, y, x, pattern, false)) score += 40;

        // 规则 4：黑白比例偏离 50%
        int dark = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (m[y, x]) dark++;
        int percent = dark * 100 / (size * size);
        score += Math.Abs(percent - 50) / 5 * 10;

        return score;
    }

    private static bool MatchRun(bool[,] m, int y, int x, int[] pattern, bool horizontal)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            // 横向：同一行往右读；纵向：同一列往下读。
            // 这里曾经写成 m[x+i, y]——行列写反，第一行就抛 IndexOutOfRange。
            bool v = horizontal ? m[y, x + i] : m[y + i, x];
            if (v != (pattern[i] == 1)) return false;
        }
        return true;
    }

    // ===== Reed-Solomon（GF(256)，本原多项式 0x11D）=====

    private static byte[] ReedSolomon(byte[] data, int ecLen)
    {
        var gen = new byte[ecLen];
        gen[ecLen - 1] = 1;
        byte root = 1;
        for (int i = 0; i < ecLen; i++)
        {
            for (int j = 0; j < ecLen; j++)
            {
                gen[j] = Mul(gen[j], root);
                if (j + 1 < ecLen) gen[j] ^= gen[j + 1];
            }
            root = Mul(root, 0x02);
        }

        var rem = new byte[ecLen];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, ecLen - 1);
            rem[ecLen - 1] = 0;
            for (int i = 0; i < ecLen; i++) rem[i] ^= Mul(gen[i], factor);
        }
        return rem;
    }

    private static byte Mul(byte a, byte b)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((b >> i) & 1) * a;
        }
        return (byte)z;
    }

    /// <summary>按位拼字节的小工具。</summary>
    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = new();

        public int Length => _bits.Count;

        public void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            return bytes;
        }
    }
}
