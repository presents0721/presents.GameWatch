using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
#endif

namespace presents.GameWatch;

/// <summary>
/// 把最近若干帧拼成一张「时间序列图」，让视觉模型能看到一段过程而不是一个瞬间。
/// 只挑「画面确实变过」的帧（frames 目录本身就是按变化落盘的），所以拼图天然跟着动作疏密走。
/// </summary>
public static class GameWatchMontage
{
    public sealed record Frame(string Path, DateTime At);

    /// <summary>
    /// 从 frames 目录取最近 window 内的帧，最多 max 张。
    /// 取的时候按时间均匀抽，避免连取三张全是同一个动作的开头。
    /// </summary>
    public static List<Frame> PickRecent(string framesDir, TimeSpan window, int max)
    {
        List<Frame> frames = new();

        try
        {
            if (!Directory.Exists(framesDir)) return frames;

            DateTime cut = DateTime.Now - window;

            foreach (string file in Directory.GetFiles(framesDir, "*.jpg"))
            {
                if (!TryParseStamp(Path.GetFileNameWithoutExtension(file), out DateTime at)) continue;
                if (at < cut) continue;

                frames.Add(new Frame(file, at));
            }
        }
        catch { return frames; }

        frames.Sort((a, b) => a.At.CompareTo(b.At));

        int limit = Math.Clamp(max, 2, 12);
        if (frames.Count <= limit) return frames;

        // 等距抽样：保留首尾，中间均匀取
        List<Frame> picked = new();
        double step = (frames.Count - 1) / (double)(limit - 1);

        for (int i = 0; i < limit; i++)
            picked.Add(frames[(int)Math.Round(i * step)]);

        return picked;
    }

    /// <summary>从文件名 yyyyMMdd_HHmmss_fff 解析时间。</summary>
    static bool TryParseStamp(string name, out DateTime at)
    {
        at = default;
        return DateTime.TryParseExact(
            name, "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out at);
    }

#if WINDOWS
    /// <summary>
    /// 拼成 cols 列的网格，每格统一缩放到 cellWidth × (按原图比例)。
    /// 每格左上角标相对第一帧的时间，让模型知道顺序和间隔。
    /// </summary>
    public static string? Compose(
        IReadOnlyList<Frame> frames, string outputPath, int cols, int cellWidth, int quality)
    {
        if (frames.Count < 2) return null;

        cols = Math.Clamp(cols, 1, 4);
        cellWidth = Math.Clamp(cellWidth, 240, 960);

        List<(Bitmap Image, string Label)> cells = new();

        try
        {
            DateTime first = frames[0].At;

            foreach (Frame f in frames)
            {
                Bitmap src;
                try { src = new Bitmap(f.Path); }
                catch { continue; }

                double seconds = (f.At - first).TotalSeconds;
                cells.Add((src, seconds < 0.05 ? "+0.0s" : $"+{seconds:0.0}s"));
            }

            if (cells.Count < 2) return null;

            // 统一按第一张的宽高比算格子高度
            double ratio = cells[0].Image.Height / (double)Math.Max(1, cells[0].Image.Width);
            int cellHeight = Math.Max(80, (int)Math.Round(cellWidth * ratio));

            int rows = (cells.Count + cols - 1) / cols;

            using Bitmap canvas = new(cols * cellWidth, rows * cellHeight);
            using Graphics g = Graphics.FromImage(canvas);

            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using Font font = new("Segoe UI", Math.Max(12, cellWidth / 32f), FontStyle.Bold);
            using SolidBrush labelBack = new(Color.FromArgb(170, 0, 0, 0));
            using SolidBrush labelText = new(Color.FromArgb(255, 255, 220, 90));

            for (int i = 0; i < cells.Count; i++)
            {
                int x = (i % cols) * cellWidth;
                int y = (i / cols) * cellHeight;

                g.DrawImage(cells[i].Image, new Rectangle(x, y, cellWidth, cellHeight));

                SizeF size = g.MeasureString(cells[i].Label, font);
                g.FillRectangle(labelBack, x + 4, y + 4, size.Width + 8, size.Height + 2);
                g.DrawString(cells[i].Label, font, labelText, x + 8, y + 5);

                // 细边框，避免相邻两格在颜色接近时糊成一整块
                using Pen border = new(Color.FromArgb(90, 255, 255, 255), 1);
                g.DrawRectangle(border, x, y, cellWidth - 1, cellHeight - 1);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? "");

            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using EncoderParameters parameters = new(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 40, 92));
            canvas.Save(outputPath, jpeg, parameters);

            return outputPath;
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach ((Bitmap image, _) in cells) { try { image.Dispose(); } catch { } }
        }
    }
#else
    public static string? Compose(
        IReadOnlyList<Frame> frames, string outputPath, int cols, int cellWidth, int quality)
        => null;
#endif

    /// <summary>给视觉模型的说明：不写这句，模型会把拼图当成几张无关的图。</summary>
    public static string Describe(IReadOnlyList<Frame> frames, int cols)
    {
        if (frames.Count < 2) return "";

        double span = (frames[^1].At - frames[0].At).TotalSeconds;

        return $"这是一段连续游戏画面按时间顺序拼接的图，{cols} 列，从左到右、从上到下依次推进，" +
               $"整段跨度约 {span:0.0} 秒，每格左上角标了相对时间。请把重点放在这 {span:0.0} 秒里" +
               "发生了什么变化（角色动作、位置移动、特效出现、血条或界面数值的变化），不要逐格描述画面。";
    }
}
