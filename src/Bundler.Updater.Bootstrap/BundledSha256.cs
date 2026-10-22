using System.Text;

namespace DotNet.Bundler.Updater.Bootstrap;

// musl 静态 AOT 形态没有可 dlopen 的 OpenSSL，System.Security.Cryptography.SHA256
// 在容器/glibc 宿主上会抛 "No usable version of libssl"（rc=134）。
// 包复核只需要确定性摘要，换 FIPS 180-4 标准变换的纯托管实现：
// 全平台单代码路径，零宿主加密依赖。
internal static class BundledSha256
{
    private static readonly uint[] K =
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    public static byte[] Hash(Stream stream)
    {
        var h = new uint[]
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        };
        var block = new byte[64];
        var readBuf = new byte[64 * 1024];
        var buffered = 0;
        long total = 0;
        int read;
        // 外圈大块读（FileStream 无用户态缓冲，64B/次的直接喂法会放大成百万级
        // syscall），内圈按 64B 整块切喂 Transform，尾余滚入 block 等下一轮补满。
        while ((read = stream.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            total += read;
            var consumed = 0;
            if (buffered > 0)
            {
                var take = Math.Min(64 - buffered, read);
                Array.Copy(readBuf, 0, block, buffered, take);
                buffered += take;
                consumed = take;
                if (buffered == 64)
                {
                    Transform(h, block, 0);
                    buffered = 0;
                }
            }
            for (; consumed + 64 <= read; consumed += 64)
            {
                Transform(h, readBuf, consumed);
            }
            if (consumed < read)
            {
                Array.Copy(readBuf, consumed, block, 0, read - consumed);
                buffered = read - consumed;
            }
        }
        // 终态填充：0x80 + 零填充到余 8 字节放 64 位大端位长。
        var bitLength = (ulong)total * 8;
        block[buffered++] = 0x80;
        if (buffered > 56)
        {
            Array.Clear(block, buffered, 64 - buffered);
            Transform(h, block, 0);
            buffered = 0;
        }
        Array.Clear(block, buffered, 56 - buffered);
        for (var i = 0; i < 8; i++)
        {
            block[56 + i] = (byte)(bitLength >> (56 - i * 8));
        }
        Transform(h, block, 0);
        var digest = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            digest[i * 4] = (byte)(h[i] >> 24);
            digest[i * 4 + 1] = (byte)(h[i] >> 16);
            digest[i * 4 + 2] = (byte)(h[i] >> 8);
            digest[i * 4 + 3] = (byte)h[i];
        }
        return digest;
    }

    public static string Hex(Stream stream)
    {
        var builder = new StringBuilder(64);
        foreach (var b in Hash(stream))
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    private static void Transform(uint[] h, byte[] block, int offset)
    {
        var w = new uint[64];
        for (var t = 0; t < 16; t++)
        {
            var p = offset + t * 4;
            w[t] = (uint)(block[p] << 24) | (uint)(block[p + 1] << 16)
                 | (uint)(block[p + 2] << 8) | block[p + 3];
        }
        for (var t = 16; t < 64; t++)
        {
            var s0 = RotR(w[t - 15], 7) ^ RotR(w[t - 15], 18) ^ (w[t - 15] >> 3);
            var s1 = RotR(w[t - 2], 17) ^ RotR(w[t - 2], 19) ^ (w[t - 2] >> 10);
            w[t] = w[t - 16] + s0 + w[t - 7] + s1;
        }
        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (var t = 0; t < 64; t++)
        {
            var bigS1 = RotR(e, 6) ^ RotR(e, 11) ^ RotR(e, 25);
            var ch = (e & f) ^ (~e & g);
            var t1 = hh + bigS1 + ch + K[t] + w[t];
            var bigS0 = RotR(a, 2) ^ RotR(a, 13) ^ RotR(a, 22);
            var maj = (a & b) ^ (a & c) ^ (b & c);
            var t2 = bigS0 + maj;
            hh = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + t2;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d;
        h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }

    private static uint RotR(uint x, int n) => (x >> n) | (x << (32 - n));
}
