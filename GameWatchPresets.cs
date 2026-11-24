using System;
using System.Collections.Generic;
using System.Linq;

namespace presents.GameWatch;

/// <summary>
/// 游戏类型预设：不同游戏要看的东西完全不同（FPS 看血量和敌人方位、RPG 看剧情对话、
/// 音游看判定和连击、建造类看进度），用同一套提示词等于让模型瞎猜。
///
/// 预设只调「看什么 / 多久看 / 变化多大才算事」和说话风格，不动窗口、隐私与开关。
/// </summary>
public static class GameWatchPresets
{
    public sealed record Preset(
        string Name,
        int IntervalSeconds,
        double DiffThreshold,
        double KeyframeThreshold,
        int Width,
        int Quality,
        string Focus,
        string Note);

    public static readonly IReadOnlyList<Preset> All = new List<Preset>
    {
        new("通用（推荐）", 6, 6, 25, 1280, 75,
            "玩家在做什么、当前目标、明显的胜负或推进",
            "不知道玩什么就先用这个：6 秒一帧，画面明显变化才看"),

        new("FPS 竞技", 8, 4, 18, 1280, 78,
            "血量/护甲/弹药、敌我人数、准星附近是否有敌人、是否倒地或阵亡、当前回合比分",
            "快节奏：8 秒一帧、阈值低，死亡/击杀/回合结束都算关键事"),

        new("MOBA 团战", 10, 4, 18, 1280, 78,
            "血量蓝量、等级与经济、兵线位置、是否在团战、击杀/死亡提示、防御塔与水晶状态",
            "10 秒一帧：团战瞬间变化大，自动标关键帧"),

        new("RPG·剧情", 20, 8, 30, 1440, 80,
            "剧情对话内容、任务目标、所在地图/场景、是否进战斗、Boss 血量阶段",
            "慢节奏：20 秒一帧、阈值高，重点是不漏剧情节点（分辨率更高，字看得清）"),

        new("音游", 6, 5, 20, 1280, 80,
            "判定（Perfect/Great/Miss）、连击数、分数、是否结算、曲名与难度",
            "快节奏：6 秒一帧，结算画面必抓关键帧"),

        new("休闲建造", 30, 10, 35, 1280, 72,
            "在建造/采集什么、资源数量、建筑进度、是否遇到危险（夜晚/怪物）",
            "慢节奏：30 秒一帧，省 token；大变化（新建筑/新区域）才细看"),
    };

    public static Preset? Find(string? name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>套用预设：只改抓帧节奏、变化阈值、截图尺寸与关注点。</summary>
    public static void Apply(Preset preset, GameWatchConfig config)
    {
        config.Preset = preset.Name;
        config.CaptureIntervalSeconds = preset.IntervalSeconds;
        config.DiffThreshold = preset.DiffThreshold;
        config.KeyframeThreshold = preset.KeyframeThreshold;
        config.MaxWidth = preset.Width;
        config.JpegQuality = preset.Quality;
        config.Focus = preset.Focus;
    }

    public static Preset? Match(GameWatchConfig config)
    {
        foreach (Preset preset in All)
        {
            if (preset.IntervalSeconds == config.CaptureIntervalSeconds
                && Math.Abs(preset.DiffThreshold - config.DiffThreshold) < 0.01
                && Math.Abs(preset.KeyframeThreshold - config.KeyframeThreshold) < 0.01
                && preset.Width == config.MaxWidth
                && preset.Quality == config.JpegQuality)
            {
                return preset;
            }
        }

        return null;
    }
}
