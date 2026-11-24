using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Alife.Function.Vision;
#endif

namespace presents.GameWatch;

/// <summary>
/// 游戏窗口截图：复用 Alife.Function.Vision 的 Windows Graphics Capture（WindowCaptureHelper），
/// 只抓指定窗口，不抓全屏。非 Windows 平台返回明确不支持。
/// </summary>
public static class GameWatchCapture
{
    public static List<WindowEntry> ListWindows()
    {
#if WINDOWS
        try
        {
            return WindowCaptureHelper.EnumerateWindows()
                .Select(w => new WindowEntry { Handle = w.Handle.ToInt64(), Title = w.Title })
                .Where(w => !string.IsNullOrWhiteSpace(w.Title))
                .ToList();
        }
        catch
        {
            return new List<WindowEntry>();
        }
#else
        return new List<WindowEntry>();
#endif
    }

    public static long ForegroundHandle()
    {
#if WINDOWS
        try { return GetForegroundWindow().ToInt64(); }
        catch { return 0; }
#else
        return 0;
#endif
    }

    public static bool IsHostFocused(string excludeKeywords)
    {
        try
        {
            long fg = ForegroundHandle();
            if (fg == 0) return false;
            WindowEntry? window = ListWindows().FirstOrDefault(w => w.Handle == fg);
            if (window == null) return false;
            string title = window.Title;
            return title.Contains("Alife", StringComparison.OrdinalIgnoreCase)
                   || title.Contains("Trae", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

#if WINDOWS
    public sealed record ObserveOptions(
        int MaxWidth, int Quality, int KeyframeWidth, int KeyframeQuality,
        double DiffThreshold, double KeyframeThreshold, string KeyframesDir);

    public sealed record ObserveResult(
        bool Ok, string Error, string Path, string Keyframe,
        double Score, string DiffText, bool Changed, bool KeyframeMade);

    /// <summary>
    /// 一次完整观察：抓窗口 → 与上一帧签名比对 → 只在画面真变时才落盘（省磁盘），
    /// 变化很大时额外存一张高清关键帧。
    /// </summary>
    public static async Task<ObserveResult> ObserveAsync(
        long handle, string outputPath, string sigPath, ObserveOptions options, bool force, string captureMethod)
    {
        (Bitmap? image, string error) = await CaptureBitmapAsync(handle, captureMethod).ConfigureAwait(false);
        if (image == null)
            return new ObserveResult(false, error, "", "", 0, "", false, false);

        try
        {
            GameWatchFrameDiff.Result diff = GameWatchFrameDiff.Compare(
                sigPath, image, options.DiffThreshold, options.KeyframeThreshold);

            // 画面没变且不是手动「看一眼」→ 不落盘、不分析
            if (!diff.Changed && !force)
                return new ObserveResult(true, "", "", "", diff.Score, diff.Difference, false, false);

            SaveScaledJpeg(image, outputPath, options.MaxWidth, options.Quality);
            GameWatchFrameDiff.SaveSignature(sigPath, image);

            string keyframe = "";

            if (diff.Keyframe)
            {
                string prefix = Path.GetFileNameWithoutExtension(outputPath);
                keyframe = GameWatchFrameDiff.SaveKeyframe(
                    image, options.KeyframesDir, prefix, options.KeyframeWidth,
                    Math.Clamp(options.KeyframeQuality, 60, 92)) ?? "";
            }

            return new ObserveResult(true, "", outputPath, keyframe, diff.Score, diff.Difference, true, keyframe.Length > 0);
        }
        finally
        {
            image.Dispose();
        }
    }
#else
    public sealed record ObserveOptions(
        int MaxWidth, int Quality, int KeyframeWidth, int KeyframeQuality,
        double DiffThreshold, double KeyframeThreshold, string KeyframesDir);

    public sealed record ObserveResult(
        bool Ok, string Error, string Path, string Keyframe,
        double Score, string DiffText, bool Changed, bool KeyframeMade);

    public static Task<ObserveResult> ObserveAsync(
        long handle, string outputPath, string sigPath, ObserveOptions options, bool force, string captureMethod)
        => Task.FromResult(new ObserveResult(false, "只有 Windows 支持游戏窗口截图", "", "", 0, "", false, false));
#endif

#if WINDOWS
    /// <summary>抓窗口原图（调用方负责 Dispose）。差异检测、关键帧、缩图都基于它。</summary>
    public static async Task<(Bitmap? Image, string Error)> CaptureBitmapAsync(long handle, string captureMethod)
    {
        try
        {
            if (handle == 0)
                return (null, "窗口句柄无效");

            string method = (captureMethod ?? "").Trim();

            // PrintWindow：不建 WGC 会话，没有截图边框/白光闪烁；失败（黑屏/独占全屏）再回退 WGC
            if (!method.Contains("WGC"))
            {
                Bitmap? printed = WindowCaptureHelper.CaptureWindowByPrint(new IntPtr(handle));
                if (printed != null)
                {
                    if (printed.Width < 8 || printed.Height < 8)
                    {
                        printed.Dispose();
                        return (null, "窗口太小，可能已最小化或不可截图");
                    }
                    return (printed, "");
                }

                if (method.Contains("PrintWindow"))
                    return (null, "PrintWindow 截图失败（窗口可能已最小化或被 GPU 独占）；可把「截图方式」换成「自动」或「WGC」");
            }

            // WGC 兜底：兼容性最好，但 Win11 会短暂显示截图边框（可能闪一下）
            Bitmap source = await WindowCaptureHelper.CaptureWindowAsync(new IntPtr(handle)).ConfigureAwait(false);

            if (source.Width < 8 || source.Height < 8)
            {
                source.Dispose();
                return (null, "窗口太小，可能已最小化或不可截图");
            }

            return (source, "");
        }
        catch (Exception e)
        {
            return (null, "截图失败：" + e.Message);
        }
    }

    /// <summary>缩放并写 JPEG（给差异层和关键帧复用）。</summary>
    public static void SaveScaledJpeg(Bitmap source, string path, int maxWidth, int quality)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");

        if (maxWidth > 0 && source.Width > maxWidth)
        {
            int width = maxWidth;
            int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
            using var resized = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(resized);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, width, height);
            SaveJpeg(resized, path, quality);
        }
        else
        {
            SaveJpeg(source, path, quality);
        }
    }

    /// <summary>给差异层用的公开 JPEG 写入。</summary>
    public static void SaveJpegPublic(Bitmap bitmap, string path, int quality)
        => SaveJpeg(bitmap, path, quality);
#endif

    public static async Task<LookResult> CaptureAsync(
        long handle, string outputPath, int maxWidth, int quality, CancellationToken ct)
    {
#if WINDOWS
        try
        {
            if (handle == 0)
                return new LookResult { Ok = false, Error = "窗口句柄无效" };

            using Bitmap source = await WindowCaptureHelper.CaptureWindowAsync(new IntPtr(handle)).ConfigureAwait(false);
            if (source.Width < 8 || source.Height < 8)
                return new LookResult { Ok = false, Error = "窗口太小，可能已最小化或不可截图" };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? "");
                if (maxWidth > 0 && source.Width > maxWidth)
                {
                    int width = maxWidth;
                    int height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
                    using var resized = new Bitmap(width, height);
                    using Graphics graphics = Graphics.FromImage(resized);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, 0, 0, width, height);
                    SaveJpeg(resized, outputPath, quality);
                }
                else
                {
                    SaveJpeg(source, outputPath, quality);
                }
            }
            catch (Exception e)
            {
                return new LookResult { Ok = false, Error = "保存截图失败：" + e.Message };
            }

            return new LookResult { Ok = true, Path = outputPath };
        }
        catch (Exception e)
        {
            return new LookResult { Ok = false, Error = "截图失败：" + e.Message };
        }
#else
        await Task.CompletedTask;
        return new LookResult { Ok = false, Error = "只有 Windows 支持游戏窗口截图" };
#endif
    }

    /// <summary>停用/暂停/换窗口时释放常驻截图会话，别让 WGC 会话一直挂着。</summary>
    public static void ReleaseCapture()
    {
#if WINDOWS
        try { WindowCaptureHelper.ReleaseAllPersistent(); } catch { }
#endif
    }

    public static void CleanupFrames(string framesDir, int maxFrames)
    {
        int keep = Math.Clamp(maxFrames, 0, 200);
        if (keep <= 0) return;
        try
        {
            var files = Directory.GetFiles(framesDir, "*.jpg")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (string file in files.Skip(keep))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // 清不掉旧图不影响截图
        }
    }

    public static int CountFrames(string framesDir)
    {
        try { return Directory.Exists(framesDir) ? Directory.GetFiles(framesDir, "*.jpg").Length : 0; }
        catch { return 0; }
    }

#if WINDOWS
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    static void SaveJpeg(Bitmap bitmap, string path, int quality)
    {
        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 30, 95));
        bitmap.Save(path, jpeg, parameters);
    }
#endif
}
