using System;
using System.IO;
#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
#endif

namespace presents.GameWatch;

/// <summary>
/// 帧间差异检测：把两张画面缩成 96×54 灰度算平均差，得到一个 0–100 的「变化度」。
///
/// 为什么值得做：固定间隔盲截 + 每张都过视觉模型 = 大量 token 花在「画面几乎没变」的帧上
/// （菜单、加载页、挂机）。用本地像素比对先把这种帧挡掉，视觉模型只处理真正的变化。
///
/// 性能：签名落盘成 .sig 伴随文件，下次直接读 5 KB 而不是解码 200 KB 的 JPEG。
/// </summary>
public static class GameWatchFrameDiff
{
    // 640×360：缩到这个尺寸仍能看清 22px 的比分/HUD 数字；再往下就糊成一片了。
    // 每帧算一次约 1–2 ms（15 秒一帧，可忽略）。
    public const int Width = 640;
    public const int Height = 360;

    public sealed class Result
    {
        public bool HasPrevious { get; init; }
        public double Score { get; init; }            // 0–100，越大变化越多
        public bool Changed { get; init; }            // Score >= 阈值
        public bool Keyframe { get; init; }           // Score >= 关键帧阈值
        public string Difference { get; init; } = ""; // 人话：变化很大/有变化/几乎没变
    }

#if WINDOWS
    /// <summary>算当前图的签名，并与上一帧签名比对。</summary>
    public static Result Compare(string previousSigPath, Bitmap current, double threshold, double keyframeThreshold)
    {
        byte[] currentSig = SignatureOf(current);
        double score = -1;

        if (!string.IsNullOrWhiteSpace(previousSigPath) && File.Exists(previousSigPath))
        {
            try
            {
                byte[] previous = File.ReadAllBytes(previousSigPath);
                if (previous.Length == currentSig.Length)
                    score = Score(previous, currentSig);
            }
            catch
            {
                // 签名文件坏了就当没有上一帧
            }
        }

        return Build(score, threshold, keyframeThreshold);
    }

    /// <summary>把签名写盘（给下一帧比对用）。</summary>
    public static void SaveSignature(string sigPath, Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sigPath) ?? "");
            File.WriteAllBytes(sigPath, SignatureOf(bitmap));
        }
        catch
        {
            // 签名存不下就退化成「下次当首帧」，不影响主流程
        }
    }

    /// <summary>96×54 灰度签名。</summary>
    public static byte[] SignatureOf(Bitmap source)
    {
        var sig = new byte[Width * Height];

        using var small = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            graphics.DrawImage(source, 0, 0, Width, Height);
        }

        BitmapData data = small.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            int stride = data.Stride;
            byte[] line = new byte[stride];
            int index = 0;

            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, line, 0, stride);

                for (int x = 0; x < Width; x++)
                {
                    int offset = x * 3;
                    int gray = (line[offset + 2] * 299 + line[offset + 1] * 587 + line[offset] * 114) / 1000;
                    sig[index++] = (byte)gray;
                }
            }
        }
        finally
        {
            small.UnlockBits(data);
        }

        return sig;
    }

    /// <summary>把整张图另存为关键帧（更高分辨率 + 更高质量）。</summary>
    public static string? SaveKeyframe(Bitmap source, string directory, string namePrefix, int maxWidth, int quality)
    {
        try
        {
            Directory.CreateDirectory(directory);

            int width = Math.Clamp(maxWidth, 160, 4096);
            Bitmap target = source;
            Bitmap? resized = null;

            if (source.Width > width)
            {
                int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
                resized = new Bitmap(width, height);
                using (Graphics graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, 0, 0, width, height);
                }
                target = resized;
            }

            string path = Path.Combine(directory, $"{namePrefix}_key.jpg");
            GameWatchCapture.SaveJpegPublic(target, path, quality);
            resized?.Dispose();

            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
#else
    public static Result Compare(string previousSigPath, object current, double threshold, double keyframeThreshold)
        => Build(-1, threshold, keyframeThreshold);

    public static void SaveSignature(string sigPath, object bitmap) { }

    public static string? SaveKeyframe(object source, string directory, string namePrefix, int maxWidth, int quality)
        => null;
#endif

    static Result Build(double score, double threshold, double keyframeThreshold)
    {
        bool hasPrevious = score >= 0;
        double value = hasPrevious ? score : 100;
        double changeThreshold = Math.Max(0.5, threshold);
        double keyThreshold = Math.Max(changeThreshold, keyframeThreshold);

        return new Result
        {
            HasPrevious = hasPrevious,
            Score = Math.Round(value, 1),
            Changed = !hasPrevious || value >= changeThreshold,
            Keyframe = hasPrevious && value >= keyThreshold,
            Difference = !hasPrevious ? "首次画面"
                : value >= keyThreshold ? "变化很大"
                : value >= changeThreshold ? "有变化"
                : "几乎没变"
        };
    }

    /// <summary>
    /// 分块差异：全图平均 + 3×3 块里的最大块均值。
    ///
    /// 为什么不用全图平均：血条、击杀提示、分数这种局部变化，缩到 96×54 后被平均掉，
    /// 实测整屏换字只算出 0.6 分（阈值 6 永远不触发）。分块取最大块能抓住「局部大变化」。
    /// </summary>
    public static (double Global, double TopBlock, double ChangedRatio) Metrics(byte[] a, byte[] b)
    {
        long total = 0;
        long changed = 0;

        for (int i = 0; i < a.Length; i++)
        {
            int diff = Math.Abs(a[i] - b[i]);
            total += diff;
            if (diff >= 24) changed++;
        }

        double global = total * 100.0 / (a.Length * 255.0);
        double ratio = changed * 100.0 / a.Length;

        // 8×8 = 64 块，取变化最大的那块（局部 HUD/提示就藏在小块里）
        const int blockWidth = Width / 8;
        const int blockHeight = Height / 8;
        double top = 0;

        for (int by = 0; by < 8; by++)
        {
            for (int bx = 0; bx < 8; bx++)
            {
                long sum = 0;
                int count = 0;

                for (int y = by * blockHeight; y < (by + 1) * blockHeight; y++)
                {
                    for (int x = bx * blockWidth; x < (bx + 1) * blockWidth; x++)
                    {
                        int index = y * Width + x;
                        sum += Math.Abs(a[index] - b[index]);
                        count++;
                    }
                }

                double mean = sum * 100.0 / (count * 255.0);
                if (mean > top) top = mean;
            }
        }

        return (global, top, ratio);
    }

    /// <summary>综合变化度：局部大变化优先，同时照顾整屏变化。</summary>
    static double Score(byte[] a, byte[] b)
    {
        (double global, double topBlock, double changedRatio) = Metrics(a, b);

        // 局部块略放大（文字/血条这种窄条在块里占比小），与全图平均取大
        double score = Math.Max(global, topBlock * 1.6);

        // 少量像素剧变也算事（准星闪一下、击杀提示）
        score = Math.Max(score, changedRatio * 1.2);

        return Math.Clamp(score, 0, 100);
    }
}
