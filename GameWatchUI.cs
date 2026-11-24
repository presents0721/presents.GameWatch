using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace presents.GameWatch;

/// <summary>
/// 游戏陪玩控制台：状态概览、快捷操作、分组配置（标签页）、截图缓存与说明。
/// </summary>
public class GameWatchUI : ModuleUIBase<GameWatchModule, GameWatchConfig>
{
    string message = "";
    bool busy;
    string activeTab = "目标";
    string previewPath = "";
    string previewData = "";

    // 游戏预设：下拉暂存 + 套用结果
    string? pendingPreset;
    string presetNote = "";

    string FramePreview()
    {
        string path = Module?.UiLastFramePath ?? "";
        if (string.Equals(path, previewPath, StringComparison.Ordinal)) return previewData;
        previewPath = path;
        previewData = "";
        try
        {
            if (path.Length > 0 && File.Exists(path) && new FileInfo(path).Length < 400_000)
                previewData = Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { }
        return previewData;
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        base.BuildRenderTree(b);
        int seq = 0;
        b.AddMarkupContent(seq++, "<style>" + Css + "</style>");

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-root");

        bool enabled = Module?.UiEnabled ?? false;
        bool paused = Module?.UiPaused ?? false;

        // ── 顶部 ──────────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-hero");
        b.AddMarkupContent(seq++, "<div class=\"gw-hero-main\">"
            + "<div class=\"gw-title\">游戏陪玩 <span class=\"gw-subtitle\">只截游戏窗口 · 视觉模型 · 默认关</span></div>"
            + "<div class=\"gw-hero-hint\">定时截取指定游戏窗口，交给视觉模型看画面，按你的节奏说一句。截图不花 token，说话才花。</div>"
            + "</div>");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-badges");
        Pill(b, ref seq, enabled ? (paused ? "已暂停" : "运行中") : "已停用", enabled && !paused);
        Pill(b, ref seq, $"今日 {Module?.UiChatToday ?? 0}/{Module?.UiChatLimit ?? 0}", enabled);
        b.CloseElement();
        b.CloseElement();

        if (!string.IsNullOrWhiteSpace(message))
            b.AddMarkupContent(seq++, $"<div class=\"gw-msg\">{E(message)}</div>");

        // ── 状态小块 ──────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-tiles");
        Tile(b, ref seq, "运行状态", enabled ? (paused ? "已暂停" : "运行中") : "已停用",
            enabled ? "截图在继续" : "不会截任何窗口");
        Tile(b, ref seq, "目标窗口", Module?.UiTargetText ?? "跟随前台窗口", "窗口标题匹配");
        Tile(b, ref seq, "今日主动说话", $"{Module?.UiChatToday ?? 0} / {Module?.UiChatLimit ?? 0}", "到上限只截图");
        Tile(b, ref seq, "截图缓存", $"{Module?.UiFrameCount ?? 0} 张", $"最近 {Module?.UiLastCaptureText ?? "—"}");
        Tile(b, ref seq, "游戏预设", Module?.UiPreset ?? "通用（推荐）", "面板「节奏」里可切换");
        Tile(b, ref seq, "上次变化", $"{Module?.UiLastDiffText ?? "—"} {(Module?.UiLastDiff ?? 0):0.#}",
            string.IsNullOrWhiteSpace(Module?.UiLastKind) ? "本地像素比对" : $"这一刻：{Module?.UiLastKind}");
        b.CloseElement();

        // ── 最近看到 ──────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-summary");
        b.AddMarkupContent(seq++, "<div class=\"gw-summary-title\">最近看到</div>");
        string summary = (Module?.UiLastSummary ?? "").Trim();
        if (summary.Length > 0)
            b.AddMarkupContent(seq++, $"<div class=\"gw-quote\">{E(summary)}</div>");
        else
            b.AddMarkupContent(seq++, "<div class=\"gw-placeholder\">还没有看过画面。开启后会自动截图，或点「现在看一眼」。</div>");
        string shot = FramePreview();
        if (shot.Length > 0)
            b.AddMarkupContent(seq++, $"<div class=\"gw-shot-wrap\"><img class=\"gw-shot\" src=\"data:image/jpeg;base64,{shot}\" alt=\"最近截图\"/></div>");
        b.CloseElement();

        // ── 事件时间轴 ────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-summary");
        b.AddMarkupContent(seq++, "<div class=\"gw-summary-title\">事件时间轴（只存结构化线索，不存像素）</div>");

        List<GameEvent> recent = Module?.UiRecentEvents(8) ?? new List<GameEvent>();

        if (recent.Count == 0)
        {
            b.AddMarkupContent(seq++, "<div class=\"gw-placeholder\">还没有事件。开起来后，画面有变化才会记录一条。</div>");
        }
        else
        {
            foreach (GameEvent item in recent)
                b.AddMarkupContent(seq++, $"<div class=\"gw-quote\" style=\"margin:4px 0;font-size:12px;\">{E(item.Line())}</div>");
        }

        b.CloseElement();

        // ── 快捷操作 ──────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-actions");
        Button(b, ref seq, enabled ? "停用陪玩" : "启用陪玩", () =>
        {
            message = Module?.UiToggleEnable() ?? "模块未激活";
            StateHasChanged();
        }, busy, primary: true);
        Button(b, ref seq, paused ? "继续截图" : "暂停截图", () =>
        {
            message = Module?.UiTogglePause() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        Button(b, ref seq, "现在看一眼", async () =>
        {
            busy = true;
            message = "正在截图/分析…";
            StateHasChanged();
            message = Module == null ? "模块未激活" : await Module.UiLookNowAsync();
            busy = false;
            StateHasChanged();
        }, busy);
        Button(b, ref seq, "列出可截窗口", () =>
        {
            message = Module?.UiListWindows() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        b.CloseElement();

        // ── 配置 ──────────────────────────────────────────────
        GameWatchConfig? cfg = Module?.Configuration;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-card");
        b.AddMarkupContent(seq++, "<div class=\"gw-card-head\"><div class=\"gw-card-title\">配置</div>"
            + "<div class=\"gw-card-hint\">改动即时生效，点「保存配置」写盘</div></div>");

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-tabs");
        Tab(b, ref seq, "目标", "目标");
        Tab(b, ref seq, "节奏", "节奏与说话");
        Tab(b, ref seq, "画面", "截图与画面");
        Tab(b, ref seq, "视觉", "视觉分析");
        b.CloseElement();

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-tab-body");

        if (activeTab == "目标")
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "gw-cfg-grid");
            ConfigBool(b, ref seq, "启用游戏陪玩", "关闭立即停止截图，不花 token。",
                cfg?.Enable ?? false, v => { if (Module != null) Module.Configuration.Enable = v; });
            ConfigBool(b, ref seq, "Alife 在前台时不截", "避免截到自己和编辑器。",
                cfg?.SkipWhenHostFocused ?? true, v => { if (Module != null) Module.Configuration.SkipWhenHostFocused = v; });
            ConfigText(b, ref seq, "游戏窗口关键字", "标题包含它才截，如 原神 / Minecraft；留空=跟随前台。",
                cfg?.WindowKeyword ?? "", false, v => { if (Module != null) Module.Configuration.WindowKeyword = v; }, true);
            ConfigText(b, ref seq, "排除窗口关键字", "逗号分隔；含这些词的窗口不截（浏览器/聊天/密码/银行等）。",
                cfg?.ExcludeKeywords ?? "", true, v => { if (Module != null) Module.Configuration.ExcludeKeywords = v; }, true);
            b.CloseElement();
        }
        else if (activeTab == "节奏")
        {
            // 游戏类型预设：一键设好节奏/阈值/尺寸/关注点
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "gw-cfg-grid");

            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "gw-field gw-wide");
            AddLabel(b, ref seq, $"游戏类型预设（当前：{Module?.UiPreset ?? "自定义"}）");

            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "style", "display:flex;gap:8px;align-items:center;flex-wrap:wrap;");

            b.OpenElement(seq++, "select");
            b.AddAttribute(seq++, "class", "gw-input");
            b.AddAttribute(seq++, "style", "flex:1;min-width:180px;cursor:pointer;");
            b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                pendingPreset = e.Value as string;
            }));

            foreach (GameWatchPresets.Preset item in GameWatchPresets.All)
            {
                b.OpenElement(seq++, "option");
                b.AddAttribute(seq++, "value", item.Name);
                if (item.Name == (Module?.UiPreset ?? "")) b.AddAttribute(seq++, "selected", true);
                b.AddContent(seq++, item.Name);
                b.CloseElement();
            }

            b.CloseElement();

            Button(b, ref seq, "套用", () =>
            {
                if (Module == null) return;
                presetNote = Module.UiApplyPreset(pendingPreset ?? Module.UiPreset);
                StateHasChanged();
            });

            b.CloseElement();

            AddHint(b, ref seq, "预设只改抓帧节奏、变化/关键帧阈值、截图尺寸和关注点，不动窗口与隐私设置。套用后仍可单项微调。");

            if (presetNote.Length > 0)
                b.AddMarkupContent(seq++, $"<div class=\"gw-hint\" style=\"color:#5b8def;margin-top:6px;\">{E(presetNote)}</div>");

            b.CloseElement();

            b.CloseElement();

            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "gw-cfg-grid");
            ConfigInt(b, ref seq, "截图间隔（秒）", "默认 6；抓帧不花 token，画面没变不会调视觉模型。",
                cfg?.CaptureIntervalSeconds ?? 6, 5, 3600, v => { if (Module != null) Module.Configuration.CaptureIntervalSeconds = v; });
            ConfigSelect(b, ref seq, "截图方式", "自动=优先 PrintWindow（不闪边框），失败回退 WGC；WGC 兼容性最好但 Win11 会闪一下截图边框。",
                cfg?.CaptureMethod ?? "自动（推荐）", new[] { "自动（推荐）", "PrintWindow", "WGC（兼容）" }, v => { if (Module != null) Module.Configuration.CaptureMethod = v; });
            ConfigDouble(b, ref seq, "变化阈值（0–100）", "默认 6；画面平均差超过它才算变了。调高更省 token。",
                cfg?.DiffThreshold ?? 6, 0.5, 100, 0.5, v => { if (Module != null) Module.Configuration.DiffThreshold = v; });
            ConfigDouble(b, ref seq, "关键帧阈值（0–100）", "默认 25；超过就另存一张高清关键帧。",
                cfg?.KeyframeThreshold ?? 25, 1, 100, 1, v => { if (Module != null) Module.Configuration.KeyframeThreshold = v; });
            ConfigInt(b, ref seq, "静止时也看一眼（分钟）", "默认 8；画面一直不变也隔这么久分析一次；0=永不看。",
                cfg?.StaticAnalyzeMinutes ?? 8, 0, 120, v => { if (Module != null) Module.Configuration.StaticAnalyzeMinutes = v; });
            ConfigSelect(b, ref seq, "陪玩风格", "默认 / 热情鼓励 / 损友调侃 / 专业解说 / 温柔陪伴，只影响语气。",
                cfg?.Style ?? "默认", new[] { "默认", "热情鼓励", "损友调侃", "专业解说", "温柔陪伴" }, v => { if (Module != null) Module.Configuration.Style = v; });
            ConfigSelect(b, ref seq, "说话方式", "every=每张都说；proactive=按间隔掷骰子，中了才说。",
                cfg?.SendMode ?? "proactive", new[] { "proactive", "every" }, v => { if (Module != null) Module.Configuration.SendMode = v; });
            ConfigInt(b, ref seq, "主动看一眼间隔（分钟）", "proactive 模式下多久掷一次骰子，默认 5。",
                cfg?.ProactiveEveryMinutes ?? 5, 1, 120, v => { if (Module != null) Module.Configuration.ProactiveEveryMinutes = v; });
            ConfigDouble(b, ref seq, "主动看一眼概率", "默认 0.35；骰子本身不花 token。",
                cfg?.ProactiveChance ?? 0.35, 0, 1, 0.05, v => { if (Module != null) Module.Configuration.ProactiveChance = v; });
            ConfigInt(b, ref seq, "每日主动说话上限", "默认 24；到上限只截图不叫醒模型。",
                cfg?.DailyLimit ?? 24, 0, 200, v => { if (Module != null) Module.Configuration.DailyLimit = v; });
            ConfigInt(b, ref seq, "两次说话最小间隔（秒）", "默认 90，避免连珠炮。",
                cfg?.MinChatIntervalSeconds ?? 90, 30, 3600, v => { if (Module != null) Module.Configuration.MinChatIntervalSeconds = v; });
            ConfigText(b, ref seq, "静默时段", "如 23:00-08:00；留空=不静默。",
                cfg?.QuietHours ?? "", false, v => { if (Module != null) Module.Configuration.QuietHours = v; }, true);
            b.CloseElement();
        }
        else if (activeTab == "画面")
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "gw-cfg-grid");
            ConfigInt(b, ref seq, "保留截图张数", "默认 20；0=不限。",
                cfg?.MaxFrames ?? 20, 0, 500, v => { if (Module != null) Module.Configuration.MaxFrames = v; });
            ConfigInt(b, ref seq, "截图宽度", "默认 1280，高度按比例。",
                cfg?.MaxWidth ?? 1280, 160, 3840, v => { if (Module != null) Module.Configuration.MaxWidth = v; });
            ConfigInt(b, ref seq, "JPEG 质量", "默认 75；越高越清楚也越大。",
                cfg?.JpegQuality ?? 75, 30, 95, v => { if (Module != null) Module.Configuration.JpegQuality = v; });
            ConfigInt(b, ref seq, "关键帧宽度", "默认 1920（比概览帧清楚，能看清文字数字）。",
                cfg?.KeyframeWidth ?? 1920, 480, 3840, v => { if (Module != null) Module.Configuration.KeyframeWidth = v; });
            ConfigInt(b, ref seq, "保留事件条数", "默认 200；只存摘要与路径，不存像素。",
                cfg?.KeepEvents ?? 200, 20, 2000, v => { if (Module != null) Module.Configuration.KeepEvents = v; });
            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "gw-cfg-grid");
            ConfigText(b, ref seq, "关注点", "告诉视觉模型该看什么：FPS 看血量/敌人/比分，RPG 看剧情/任务/Boss 阶段。",
                cfg?.Focus ?? "", true, v => { if (Module != null) Module.Configuration.Focus = v; }, true);
            ConfigBool(b, ref seq, "赛前功课",
                "第一次陪玩某款游戏时，先让 AI 了解它（类型/玩法/剧情/术语/事件节点），存成笔记，之后陪玩自动带上。",
                cfg?.PreGameStudy ?? true, v => { if (Module != null) Module.Configuration.PreGameStudy = v; });
            ConfigInt(b, ref seq, "功课注入上限（字）", "默认 600；太长会占上下文。",
                cfg?.KnowledgeMaxChars ?? 600, 100, 3000, v => { if (Module != null) Module.Configuration.KnowledgeMaxChars = v; });
            ConfigInt(b, ref seq, "视觉识图超时（秒）", "默认 10；超时立刻退回本机 OCR（更快但只读文字）。",
                cfg?.VisionTimeoutSeconds ?? 10, 3, 60, v => { if (Module != null) Module.Configuration.VisionTimeoutSeconds = v; });
            ConfigInt(b, ref seq, "视觉回复长度", "默认 160（结构化四行够用）。",
                cfg?.VisionMaxTokens ?? 160, 60, 400, v => { if (Module != null) Module.Configuration.VisionMaxTokens = v; });
            ConfigBool(b, ref seq, "离线 OCR 兜底",
                "视觉模型没配或没返回内容时，用 Windows 自带 OCR 读画面文字（血量/比分/任务/台词）；零依赖、不出网。",
                cfg?.UseLocalOcr ?? true, v => { if (Module != null) Module.Configuration.UseLocalOcr = v; });
            ConfigText(b, ref seq, "OCR 语言", "默认 zh-Hans；英文游戏用 en-US。",
                cfg?.OcrLanguage ?? "zh-Hans", false, v => { if (Module != null) Module.Configuration.OcrLanguage = v; });
            ConfigInt(b, ref seq, "工具结果回注上限（字）", "默认 3000；限制单条 Poke 的最大字数。",
                cfg?.PokeMaxChars ?? 3000, 500, 10000, v => { if (Module != null) Module.Configuration.PokeMaxChars = v; });
            b.CloseElement();
        }

        b.CloseElement();

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-config-footer");
        Button(b, ref seq, "保存配置", () =>
        {
            message = Module?.UiSaveConfig() ?? "模块未激活";
            StateHasChanged();
        }, busy, primary: true);
        Button(b, ref seq, "刷新", () =>
        {
            message = "";
            StateHasChanged();
        }, busy);
        b.CloseElement();
        b.CloseElement();

        // ── 截图与说明 ────────────────────────────────────────
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-card");
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "gw-details");
        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, "截图缓存与使用说明");
        b.CloseElement();
        b.AddMarkupContent(seq++, $"<div class=\"gw-muted\">截图目录：<code>{E(Module?.UiFramesDir ?? "")}</code></div>");
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-row");
        Button(b, ref seq, "打开截图目录", () =>
        {
            message = Module?.UiOpenFramesFolder() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        Button(b, ref seq, "清空截图", () =>
        {
            message = Module?.UiClearFrames() ?? "模块未激活";
            StateHasChanged();
        }, busy);
        b.CloseElement();
        b.AddMarkupContent(seq++, """
            <div class="gw-line">· 先开启「启用游戏陪玩」，填「游戏窗口关键字」（如 原神 / Minecraft）；留空=跟随前台窗口。</div>
            <div class="gw-line">· 截图只截指定游戏窗口，不截全屏；浏览器/聊天/密码/银行窗口默认排除。</div>
            <div class="gw-line">· 说话方式：every=每张都说；proactive=每 N 分钟掷一次骰子，中了才看+说（默认）。</div>
            <div class="gw-line">· 截图不花 token；视觉分析和主动说话才花。默认 5 分钟 / 0.35 概率 / 每日 24 次。</div>
            <div class="gw-line">· 独占全屏（DX 独占）游戏可能截出黑屏；改成无边框窗口通常可解。默认关，随时可暂停。</div>
            <div class="gw-line">· 依赖 Alife.Function.Vision：窗口截图用它，视觉分析用你配置的视觉模型。</div>
            """);
        b.CloseElement();
        b.CloseElement();

        b.AddMarkupContent(seq++, $"<div class=\"gw-footer\">最近截图：{E(Module?.UiLastFramePath ?? "")}</div>");
        b.CloseElement();
    }

    // ── 组件 ─────────────────────────────────────────────────

    static void Pill(RenderTreeBuilder b, ref int seq, string text, bool on)
    {
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", on ? "gw-pill gw-pill-on" : "gw-pill");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    static void Tile(RenderTreeBuilder b, ref int seq, string label, string value, string sub)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-tile");
        b.AddMarkupContent(seq++, $"<div class=\"gw-tile-label\">{E(label)}</div>");
        b.AddMarkupContent(seq++, $"<div class=\"gw-tile-value\">{E(value)}</div>");
        b.AddMarkupContent(seq++, $"<div class=\"gw-tile-sub\">{E(sub)}</div>");
        b.CloseElement();
    }

    void Tab(RenderTreeBuilder b, ref int seq, string key, string label)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "class", activeTab == key ? "gw-tab gw-tab-on" : "gw-tab");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () =>
        {
            activeTab = key;
            StateHasChanged();
        }));
        b.AddContent(seq++, label);
        b.CloseElement();
    }

    // ── 配置控件 ─────────────────────────────────────────────

    void ConfigBool(RenderTreeBuilder b, ref int seq, string label, string hint, bool value, Action<bool> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-field");
        b.OpenElement(seq++, "label");
        b.AddAttribute(seq++, "class", "gw-check");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "checkbox");
        b.AddAttribute(seq++, "checked", value);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            onChange(e.Value is bool flag && flag);
        }));
        b.CloseElement();
        b.AddContent(seq++, " " + label);
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigInt(RenderTreeBuilder b, ref int seq, string label, string hint, int value, int min, int max, Action<int> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-field");
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "gw-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "min", min);
        b.AddAttribute(seq++, "max", max);
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (int.TryParse(e.Value?.ToString(), out int parsed)) onChange(Math.Clamp(parsed, min, max));
        }));
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigDouble(RenderTreeBuilder b, ref int seq, string label, string hint, double value, double min, double max, double step, Action<double> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-field");
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "class", "gw-input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "min", min);
        b.AddAttribute(seq++, "max", max);
        b.AddAttribute(seq++, "step", step);
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            if (double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                onChange(Math.Clamp(parsed, min, max));
        }));
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigText(RenderTreeBuilder b, ref int seq, string label, string hint, string value, bool multiline, Action<string> onChange, bool wide = false)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", wide ? "gw-field gw-wide" : "gw-field");
        AddLabel(b, ref seq, label);
        if (multiline)
        {
            b.OpenElement(seq++, "textarea");
            b.AddAttribute(seq++, "class", "gw-input");
            b.AddAttribute(seq++, "rows", 3);
            b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                onChange(e.Value?.ToString() ?? "");
            }));
            b.AddContent(seq++, value);
            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "input");
            b.AddAttribute(seq++, "class", "gw-input");
            b.AddAttribute(seq++, "type", "text");
            b.AddAttribute(seq++, "value", value);
            b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                onChange(e.Value?.ToString() ?? "");
            }));
            b.CloseElement();
        }
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    void ConfigSelect(RenderTreeBuilder b, ref int seq, string label, string hint, string value, string[] options, Action<string> onChange)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-field");
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "select");
        b.AddAttribute(seq++, "class", "gw-input");
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            onChange(e.Value?.ToString() ?? "");
        }));
        foreach (string option in options)
        {
            b.OpenElement(seq++, "option");
            b.AddAttribute(seq++, "value", option);
            b.AddAttribute(seq++, "selected", string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
            b.AddContent(seq++, option);
            b.CloseElement();
        }
        b.CloseElement();
        AddHint(b, ref seq, hint);
        b.CloseElement();
    }

    static void AddLabel(RenderTreeBuilder b, ref int seq, string label)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-field-label");
        b.AddContent(seq++, label);
        b.CloseElement();
    }

    static void AddHint(RenderTreeBuilder b, ref int seq, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "gw-field-hint");
        b.AddContent(seq++, hint);
        b.CloseElement();
    }

    void Button(RenderTreeBuilder b, ref int seq, string label, Action handler, bool disabled = false, bool primary = false)
        => Button(b, ref seq, label, () => { handler(); return Task.CompletedTask; }, disabled, primary);

    void Button(RenderTreeBuilder b, ref int seq, string label, Func<Task> handler, bool disabled = false, bool primary = false)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "class", primary ? "gw-btn gw-btn-primary" : "gw-btn");
        if (disabled) b.AddAttribute(seq++, "disabled", true);
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, handler));
        b.AddContent(seq++, label);
        b.CloseElement();
    }

    static string E(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");

    const string Css = """

        .gw-root{font-family:"Microsoft YaHei UI","Segoe UI",sans-serif;color:#1f2b3a;padding:20px;max-width:1040px;margin:0 auto;background:linear-gradient(180deg,#f7fafd 0%,#ffffff 320px)}
        .gw-hero{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;padding:18px 20px;border:1px solid #dbe5f0;border-left:4px solid #2f6fed;border-radius:14px;background:linear-gradient(135deg,#ffffff 0%,#f3f8ff 100%);box-shadow:0 2px 10px rgba(47,111,237,.06)}
        .gw-title{font-size:22px;font-weight:700;letter-spacing:.2px}
        .gw-subtitle{font-size:12px;font-weight:400;color:#7b8798;margin-left:6px}
        .gw-hero-hint{font-size:12px;color:#5b6b7f;margin-top:6px;line-height:1.6}
        .gw-badges{display:flex;gap:6px;flex-wrap:wrap;padding-top:4px}
        .gw-pill{font-size:11px;padding:4px 10px;border-radius:999px;background:#eef2f7;color:#64748b;border:1px solid #e2e8f0;white-space:nowrap}
        .gw-pill-on{background:#e8f7ee;color:#17803f;border-color:#c8ecd5}
        .gw-msg{margin:12px 0 0;padding:10px 12px;border-radius:10px;background:#eef6ff;color:#1f4f8b;white-space:pre-wrap;font-size:13px}
        .gw-muted{color:#7b8798;font-size:12px;font-weight:400}
        .gw-tiles{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin-top:14px}
        .gw-tile{padding:12px 14px;border:1px solid #e3ebf4;border-radius:12px;background:#fff}
        .gw-tile-label{font-size:11px;color:#8a97a8;font-weight:600}
        .gw-tile-value{font-size:18px;font-weight:700;margin-top:4px;color:#1e3a5f;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .gw-tile-sub{font-size:11px;color:#94a3b8;margin-top:3px}
        .gw-summary{margin-top:14px;padding:14px 16px;border:1px solid #e3ebf4;border-radius:12px;background:#fff}
        .gw-summary-title{font-size:11px;color:#8a97a8;font-weight:700;letter-spacing:.4px;margin-bottom:6px}
        .gw-quote{font-size:13px;line-height:1.7;color:#334155;background:#f7f9fc;border-left:3px solid #9cb6e8;border-radius:0 8px 8px 0;padding:8px 12px;white-space:pre-wrap}
        .gw-placeholder{font-size:12px;color:#94a3b8}
        .gw-shot-wrap{margin-top:10px}
        .gw-shot{display:block;width:100%;max-height:260px;object-fit:contain;border-radius:10px;border:1px solid #e3ebf4;background:#0f172a}
        .gw-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:14px}
        .gw-btn{border:1px solid #cbd6e3;background:#fff;border-radius:9px;padding:8px 13px;cursor:pointer;font-size:12px;color:#334155;transition:background .15s,border-color .15s}
        .gw-btn:hover{background:#f3f7fd;border-color:#a9c0e4}
        .gw-btn:disabled{opacity:.55;cursor:default}
        .gw-btn-primary{background:#2f6fed;border-color:#2f6fed;color:#fff}
        .gw-btn-primary:hover{background:#2560d4;border-color:#2560d4}
        .gw-card{border:1px solid #e3ebf4;border-radius:14px;padding:16px 18px;margin-top:16px;background:#fff;box-shadow:0 1px 4px rgba(30,58,95,.04)}
        .gw-card-head{display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:10px}
        .gw-card-title{font-weight:700;font-size:14px}
        .gw-card-hint{font-size:11px;color:#94a3b8}
        .gw-tabs{display:flex;gap:4px;flex-wrap:wrap;padding:4px;background:#f3f6fa;border-radius:10px;margin-bottom:14px}
        .gw-tab{border:0;background:transparent;border-radius:7px;padding:6px 12px;font-size:12px;color:#64748b;cursor:pointer}
        .gw-tab:hover{color:#2560d4}
        .gw-tab-on{background:#fff;color:#1f4f8b;font-weight:700;box-shadow:0 1px 3px rgba(30,58,95,.12)}
        .gw-cfg-grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:14px 18px}
        .gw-field{display:flex;flex-direction:column;gap:4px;min-width:0}
        .gw-wide{grid-column:1/-1}
        .gw-field-label{font-size:12px;font-weight:600;color:#334155}
        .gw-field-hint{font-size:11px;color:#8a97a8;line-height:1.5}
        .gw-check{display:flex;align-items:center;gap:6px;font-size:12px;font-weight:600;color:#334155;cursor:pointer}
        .gw-input{width:100%;box-sizing:border-box;border:1px solid #d3deea;border-radius:8px;padding:7px 9px;font-family:inherit;font-size:12px;color:#1f2b3a;background:#fbfdff}
        .gw-input:focus{outline:2px solid #cfe0fb;border-color:#9dbcf0}
        textarea.gw-input{resize:vertical;line-height:1.55}
        .gw-config-footer{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end;margin-top:16px;padding-top:12px;border-top:1px dashed #e6edf5}
        .gw-details summary{cursor:pointer;font-size:13px;font-weight:700;color:#334155;padding:2px 0}
        .gw-details[open] summary{margin-bottom:10px}
        .gw-row{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}
        .gw-line{font-size:12px;line-height:1.85;color:#4b5b6f;margin-top:6px}
        .gw-footer{margin-top:14px;font-size:11px;color:#a3aebd;word-break:break-all}
        code{background:#f1f5f9;border-radius:4px;padding:1px 5px;color:#475569}
        @media (max-width:860px){
            .gw-tiles{grid-template-columns:repeat(2,minmax(0,1fr))}
            .gw-cfg-grid{grid-template-columns:1fr}
            .gw-wide{grid-column:1}
            .gw-hero{flex-direction:column}
        }
        """;
}
