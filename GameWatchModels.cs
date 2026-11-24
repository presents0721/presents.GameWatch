using System;
using System.Collections.Generic;
using System.Linq;

namespace presents.GameWatch;

public sealed class WindowEntry
{
    public long Handle { get; init; }
    public string Title { get; init; } = "";
}

public sealed class LookResult
{
    public bool Ok { get; init; }
    public string Path { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Error { get; init; } = "";

    // ── 结构化观察（v2）──
    public string Game { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Situation { get; init; } = "";
    public string Change { get; init; } = "";
    public string Keyframe { get; init; } = "";
    public double DiffScore { get; init; }
    public string DiffText { get; init; } = "";
    public bool Analyzed { get; init; }          // 是否真的调了视觉模型（没变化时为 false）
    public string SkipReason { get; init; } = ""; // 没分析的原因（画面没变 / 太频繁）

    public bool HasClue => Game.Length > 0 || Situation.Length > 0 || Change.Length > 0;
}

/// <summary>
/// 窗口选择：先按关键字找，找不到就跟随前台；排除浏览器/聊天/密码窗口。
/// 纯逻辑，方便测试；真正截图在 GameWatchCapture。
/// </summary>
public static class GameWindowMatcher
{
    public static bool IsExcluded(string title, string excludeKeywords)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        foreach (string word in Split(excludeKeywords))
        {
            if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static WindowEntry? Find(
        IReadOnlyList<WindowEntry> windows,
        string keyword,
        string excludeKeywords,
        long foregroundHandle)
    {
        if (windows == null || windows.Count == 0) return null;

        string key = (keyword ?? "").Trim();
        if (key.Length > 0)
        {
            // 关键字优先：取最后一个匹配的（通常是最新打开的那个）。
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                WindowEntry window = windows[i];
                if (window.Title.Contains(key, StringComparison.OrdinalIgnoreCase)
                    && !IsExcluded(window.Title, excludeKeywords))
                    return window;
            }
            return null;
        }

        WindowEntry? foreground = windows.FirstOrDefault(w => w.Handle == foregroundHandle);
        if (foreground == null) return null;
        return IsExcluded(foreground.Title, excludeKeywords) ? null : foreground;
    }

    public static string[] Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        return value.Split(new[] { ',', '，', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
    }
}
