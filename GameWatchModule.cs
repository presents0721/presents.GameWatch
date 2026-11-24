using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Vision;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Presents.Shared.Media;

namespace presents.GameWatch;

/// <summary>
/// 游戏陪玩：盯住游戏窗口，先本地比对画面变化，真变了才让视觉模型抽取结构化线索，
/// 再按「关键时刻必说 / 平时按概率」的策略开口。
///
/// 三个关键设计（都是踩过坑才定下来的）：
///   1. 变化门控：640×360 灰度分块比对（约 5 ms/帧）。画面没变不调视觉模型——
///      固定 15 秒盲截 + 每张都分析，绝大部分 token 花在菜单和挂机上。
///   2. 识图与说话拆两步：视觉模型只出四行结构化线索（游戏/局势/变化/事件），
///      由主模型结合游戏常识组织语言。否则它会把 UI 逐项念一遍（"画面里有血条"）。
///   3. 事件时间轴：每条线索落盘（摘要+路径，不存像素），事后能复盘「刚才打得怎么样」。
///
/// 截图用 Alife.Function.Vision 的 Windows Graphics Capture（只抓窗口，不抓全屏）；
/// 默认关，不截浏览器/聊天/密码窗口，不读内存、不注入进程。
/// </summary>
[Module("游戏陪玩",
    "游戏陪玩：盯住游戏窗口，先本地比对画面变化（没变就不花 token），变了才让视觉模型抽取结构化线索（游戏/局势/变化/事件），"
    + "再按「关键时刻必说、平时按概率」开口。带游戏类型预设（FPS/MOBA/RPG/音游/休闲）、事件时间轴与复盘，"
    + "只截指定窗口、默认关、排除浏览器与聊天窗口。",
    defaultCategory: "presents/陪伴",
    editorUI: typeof(GameWatchUI))]
public partial class GameWatchModule(
    XmlFunctionCaller functionCaller,
    Interactor<GameWatchModule> interactor,
    PluginSystem pluginSystem,
    ILogger<GameWatchModule> logger,
    IVisionModel? visionModel = null,
    ILanguageModel? languageModel = null) :
    ChatBehaviour,
    IConfigurable<GameWatchConfig>
{
    public const string Version = "2.0.0";
    public GameWatchConfig Configuration { get; set; } = new();

    readonly SemaphoreSlim gate = new(1, 1);

    string pluginDir = "";
    string dataDir = "";
    string framesDir = "";
    string keyframesDir = "";

    float tickAccum;
    bool paused;
    bool wasEnabled;
    bool waking;

    DateTime lastCaptureAt = DateTime.MinValue;
    DateTime lastChatAt = DateTime.MinValue;
    DateTime lastProactiveAt = DateTime.MinValue;
    string dayKey = "";
    int chatCount;

    string lastWindowTitle = "";
    long lastTargetHandle;
    string lastFramePath = "";
    string lastSummary = "";
    string lastError = "";

    // 结构化观察状态（v2）
    GameEventStore? events;
    string lastSigPath = "";
    string lastGame = "";
    string lastKind = "";
    string lastSituation = "";

    /// <summary>最近几轮的局面轨迹。单帧只能回答「现在是什么样」，轨迹才能回答「比刚才怎么样」。</summary>
    readonly GameWatchNarrative narrative = new();
    double lastDiff;
    string lastDiffText = "";
    DateTime lastAnalyzeAt = DateTime.MinValue;

    // 赛前功课（按游戏名存本地，陪玩时注入 System 上下文）
    GameKnowledgeStore? knowledge;
    GameKnowledge? activeKnowledge;
    string activeTitleKey = "";
    string lastStudyGame = "";
    DateTime lastStudyAt = DateTime.MinValue;

    // 模型正忙时没说完的画面：留到下一拍再试，而不是直接丢掉
    LookResult? pendingLook;
    DateTime pendingLookAt = DateTime.MinValue;
    bool pendingLookKeyEvent;

    public GameEventStore? UiEvents => events;
    public string UiPreset => GameWatchPresets.Match(Configuration)?.Name ?? "自定义";
    public double UiLastDiff => lastDiff;
    public string UiLastDiffText => lastDiffText;
    public string UiLastKind => lastKind;
    public string UiLastGame => lastGame;

    public string UiFramesDir => framesDir;
    public string UiLastFramePath => lastFramePath;
    public string UiLastSummary => lastSummary;
    public bool UiEnabled => Configuration.Enable;
    public bool UiPaused => paused;
    public string UiTargetText => string.IsNullOrWhiteSpace(Configuration.WindowKeyword)
        ? "跟随前台窗口"
        : Configuration.WindowKeyword.Trim();
    public int UiChatToday => chatCount;
    public int UiChatLimit => Math.Max(0, Configuration.DailyLimit);
    public int UiFrameCount => GameWatchCapture.CountFrames(framesDir);
    public string UiLastCaptureText => lastCaptureAt == default ? "还没截过" : lastCaptureAt.ToString("HH:mm:ss");

    // ─────────────────────────────────────────────────────────────
    // 生命周期
    // ─────────────────────────────────────────────────────────────

    protected override Task OnAwake()
    {
        try
        {
            string pluginId = pluginSystem.PluginContext.GetTypeAttachedPlugin(typeof(GameWatchModule))
                              ?? "presents.GameWatch";
            pluginDir = pluginSystem.PluginContext.GetPluginDirectoryPath(pluginId);
            dataDir = Path.Combine(AlifePath.StorageFolderPath, "PluginData", "presents.GameWatch");
            framesDir = Path.Combine(dataDir, "frames");
            keyframesDir = Path.Combine(dataDir, "keyframes");
            Directory.CreateDirectory(framesDir);
            Directory.CreateDirectory(keyframesDir);
            events ??= new GameEventStore(Path.Combine(dataDir, "events.json"));
            knowledge ??= new GameKnowledgeStore(dataDir);

            XmlHandler handler = new(this)
            {
                Description = "游戏陪玩：盯住游戏窗口，画面变了才让视觉模型抽取线索（游戏/局势/变化/事件），按策略开口；支持预设、事件时间轴与复盘。",
                Explanation =
                    "使用方式：\n"
                    + "1. 主人说「陪我打游戏」→ 先确认控制台已开启「启用游戏陪玩」，填好「游戏窗口关键字」；"
                    + "   没填就跟随当前前台窗口。\n"
                    + "2. 插件每 N 秒抓一帧，先在本地比对画面（不动模型）；画面真变了才让视觉模型出四行线索。\n"
                    + "3. 常用调用：game_watch(look=true) 现在看一眼；statusOnly=true 看状态；"
                    + "   timeline=true 看最近事件；recap=true&minutes=30 复盘最近半小时；"
                    + "   preset=\"FPS 竞技\" 切游戏类型；pause/resume 暂停/继续；listWindows=true 列窗口；"
                    + "   window=\"关键字\" 临时改目标。\n"
                    + "4. 你说的话要基于线索，不要复述画面（「画面里有血条」这种），不要编看不到的数字。\n"
                    + "5. 关键节点（胜利/失败/结算/升级/死亡/大幅变化）会优先叫你；其余按概率，有冷却与每日上限。\n"
                    + "6. 只截游戏窗口；浏览器/聊天/密码/银行窗口默认排除，截图与事件只留在本机 PluginData。"
            };
            WrapHandlerResultsForPoke(handler);
            functionCaller.RegisterHandler(handler, DocumentMode.Implicit, DestroyCancellationToken);

            logger.LogInformation("游戏陪玩：已唤醒，默认{State}", Configuration.Enable ? "开启" : "关闭");
        }
        catch (Exception e)
        {
            logger.LogError(e, "游戏陪玩：OnAwake 失败");
        }
        return Task.CompletedTask;
    }

    protected override Task OnUpdate()
    {
        try
        {
            if (!Configuration.Enable)
            {
                if (wasEnabled) GameWatchCapture.ReleaseCapture();
                wasEnabled = false;
                return Task.CompletedTask;
            }

            if (!wasEnabled)
            {
                // 刚开启先安静一个间隔，别一开就截/就说。
                wasEnabled = true;
                DateTime now = DateTime.Now;
                lastCaptureAt = now;
                lastProactiveAt = now;
                return Task.CompletedTask;
            }

            if (paused) return Task.CompletedTask;
            if (InQuietHours(DateTime.Now)) return Task.CompletedTask;

            tickAccum += UpdateContext.DeltaTime;
            if (tickAccum < 2f) return Task.CompletedTask;
            tickAccum = 0;

            // 上一次因为模型正忙没说完的画面，空闲了就补一句（太旧就丢）
            if (TryFlushPendingTalk()) return Task.CompletedTask;

            if ((DateTime.Now - lastCaptureAt).TotalSeconds < Math.Max(5, Configuration.CaptureIntervalSeconds))
                return Task.CompletedTask;

            lastCaptureAt = DateTime.Now;
            _ = CaptureAndMaybeTalkAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "游戏陪玩：OnUpdate 失败");
        }
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        GameWatchCapture.ReleaseCapture();
        gate.Dispose();
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────
    // 截图 / 视觉 / 说话
    // ─────────────────────────────────────────────────────────────

    async Task<LookResult?> CaptureAndMaybeTalkAsync()
    {
        bool acquired = false;
        try { acquired = await gate.WaitAsync(0, DestroyCancellationToken).ConfigureAwait(false); }
        catch { return null; }
        if (!acquired) return null;
        try
        {
            LookResult result = await CaptureOnceAsync().ConfigureAwait(false);
            if (!result.Ok)
            {
                lastError = result.Error;
                return result;
            }

            // 关键节点（胜负/结算/升级/死亡/大幅变化/局面转折）优先开口。
            // 转折这一条是后加的：画面变化可能不大（切了个镜头），但局势从「探索」变成「遭遇战」，
            // 这种时候不说话，等下一轮再说就晚了。
            bool keyEvent = result.Analyzed
                            && (result.Kind is "胜利" or "失败" or "结算" or "升级" or "死亡"
                                || result.DiffScore >= Math.Max(1, Configuration.KeyframeThreshold)
                                || (Configuration.TalkOnTurn && narrative.JustTurned()));

            await MaybeTalkAsync(result, keyEvent).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<LookResult> LookOnceAsync()
    {
        bool acquired = false;
        try { acquired = await gate.WaitAsync(0, DestroyCancellationToken).ConfigureAwait(false); }
        catch { return new LookResult { Ok = false, Error = "插件正在关闭" }; }
        if (!acquired)
            return new LookResult { Ok = false, Error = "正在截图，稍后再试" };
        try
        {
            // 手动「看一眼」强制分析：即使画面没变，主人也可能就是想让你看看
            return await CaptureOnceAsync(force: true).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 抓一帧并（按需）分析。
    /// 关键：先本地比对变化，画面没变就不调视觉模型——这是省 token 的主要来源。
    /// </summary>
    async Task<LookResult> CaptureOnceAsync(bool force = false)
    {
        lastCaptureAt = DateTime.Now;
        try
        {
            List<WindowEntry> windows = GameWatchCapture.ListWindows();
            long foreground = GameWatchCapture.ForegroundHandle();

            if (Configuration.SkipWhenHostFocused && GameWatchCapture.IsHostFocused(Configuration.ExcludeKeywords))
                return new LookResult { Ok = false, Error = "当前前台是 Alife/编辑器窗口，跳过截图" };

            WindowEntry? target = GameWindowMatcher.Find(
                windows, Configuration.WindowKeyword, Configuration.ExcludeKeywords, foreground);
            if (target == null)
            {
                string error = string.IsNullOrWhiteSpace(Configuration.WindowKeyword)
                    ? "当前前台窗口在排除列表里，或没有可截窗口；可以调 game_watch(listWindows=true) 看列表"
                    : $"没找到标题包含「{Configuration.WindowKeyword.Trim()}」的窗口";
                return new LookResult { Ok = false, Error = error };
            }

            // 换游戏/第一次看到这个窗口标题：把赛前功课找出来（没有就触发一次功课）
            if (target.Handle != lastTargetHandle)
            {
                lastTargetHandle = target.Handle;
                GameWatchCapture.ReleaseCapture();   // 换窗口：旧会话立刻释放
            }
            EnsureKnowledge(target.Title);

            // 静止太久也要看一眼，避免挂机/加载被永久忽略
            int staticMinutes = Math.Max(0, Configuration.StaticAnalyzeMinutes);
            bool staticDue = staticMinutes > 0
                             && lastAnalyzeAt != DateTime.MinValue
                             && (DateTime.Now - lastAnalyzeAt).TotalMinutes >= staticMinutes;

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string path = Path.Combine(framesDir, stamp + ".jpg");
            string sigPath = Path.Combine(framesDir, "latest.sig");

            GameWatchCapture.ObserveResult observed = await GameWatchCapture.ObserveAsync(
                target.Handle, path, sigPath,
                new GameWatchCapture.ObserveOptions(
                    Math.Clamp(Configuration.MaxWidth, 160, 3840),
                    Math.Clamp(Configuration.JpegQuality, 30, 95),
                    Math.Clamp(Configuration.KeyframeWidth, 480, 3840),
                    Math.Clamp(Configuration.JpegQuality + 10, 60, 92),
                    Math.Clamp(Configuration.DiffThreshold, 0.5, 100),
                    Math.Clamp(Configuration.KeyframeThreshold, 1, 100),
                    keyframesDir),
                force || staticDue, Configuration.CaptureMethod).ConfigureAwait(false);

            lastWindowTitle = target.Title;
            lastDiff = observed.Score;
            lastDiffText = observed.DiffText;

            if (!observed.Ok)
            {
                lastError = observed.Error;
                return new LookResult { Ok = false, Error = observed.Error };
            }

            // 画面没变：不分析，只回报「没变化」
            if (!observed.Changed)
            {
                lastError = "";
                return new LookResult
                {
                    Ok = true,
                    WindowTitle = target.Title,
                    Game = lastGame,
                    Kind = lastKind,
                    Situation = lastSituation,
                    DiffScore = observed.Score,
                    DiffText = observed.DiffText,
                    Analyzed = false,
                    SkipReason = $"画面没变化（{observed.DiffText} {observed.Score:0.#}）"
                };
            }

            // 变化够剧烈时改用「时序拼图」：单帧只能说有什么，拼图能说发生了什么
            string analyzePath = observed.Path;
            string montageHint = "";

            if (Configuration.EnableMontage && observed.Score >= Configuration.MontageMinDiff)
            {
                try
                {
                    List<GameWatchMontage.Frame> recent = GameWatchMontage.PickRecent(
                        framesDir,
                        TimeSpan.FromSeconds(Math.Clamp(Configuration.MontageWindowSeconds, 2, 60)),
                        Math.Clamp(Configuration.MontageFrames, 3, 12));

                    if (recent.Count >= 3)
                    {
                        string montagePath = Path.Combine(framesDir, "montage_" + stamp + ".jpg");
                        string? made = GameWatchMontage.Compose(
                            recent, montagePath,
                            Math.Clamp(Configuration.MontageColumns, 1, 4),
                            Math.Clamp(Configuration.MontageCellWidth, 240, 960),
                            Configuration.JpegQuality);

                        if (made != null)
                        {
                            analyzePath = made;
                            montageHint = GameWatchMontage.Describe(recent, Configuration.MontageColumns);
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.LogDebug(e, "游戏陪玩：拼图失败，回退单帧");
                }
            }

            GameWatchVision.Clue clue = await AnalyzeAsync(analyzePath, montageHint).ConfigureAwait(false);

            lastGame = clue.Game.Length > 0 ? clue.Game : lastGame;
            // 先入轨迹再更新 last* —— 「沿用上一轮」的兜底会抹掉这一轮的真实内容，
            // 轨迹必须记原样，否则趋势判断永远看不出变化。
            narrative.Add(clue);

            lastKind = clue.Kind.Length > 0 ? clue.Kind : lastKind;
            lastSituation = clue.Situation.Length > 0 ? clue.Situation : lastSituation;
            lastSummary = BuildSummary(clue);
            lastFramePath = observed.Path;
            lastError = "";
            lastAnalyzeAt = DateTime.Now;

            events?.Add(new GameEvent
            {
                At = DateTime.Now,
                Game = lastGame,
                Kind = lastKind,
                Situation = clue.Situation,
                Change = clue.Change,
                DiffText = observed.DiffText,
                DiffScore = observed.Score,
                Frame = observed.Path,
                Keyframe = observed.Keyframe
            }, Configuration.KeepEvents);

            GameWatchCapture.CleanupFrames(framesDir, Configuration.MaxFrames);

            return new LookResult
            {
                Ok = true,
                Path = observed.Path,
                WindowTitle = target.Title,
                Summary = lastSummary,
                Game = lastGame,
                Kind = lastKind,
                Situation = clue.Situation,
                Change = clue.Change,
                Keyframe = observed.Keyframe,
                DiffScore = observed.Score,
                DiffText = observed.DiffText,
                Analyzed = true
            };
        }
        catch (Exception e)
        {
            lastError = e.Message;
            logger.LogDebug(e, "游戏陪玩：截图失败");
            return new LookResult { Ok = false, Error = e.Message };
        }
    }

    /// <summary>结构化抽取（只要四行线索，不要画面描述）。</summary>
    async Task<GameWatchVision.Clue> AnalyzeAsync(string path, string montageHint = "")
    {
        if (visionModel != null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Configuration.VisionTimeoutSeconds, 3, 60)));

                string prompt = GameWatchVision.BuildPrompt(
                    Configuration, lastSituation, lastKind, activeKnowledge?.Notes,
                    montageHint, narrative.Trace(), narrative.Stagnant());
                string answer = await visionModel.QueryAsync(
                    path,
                    prompt,
                    Math.Clamp(Configuration.VisionMaxTokens, 60, 400),
                    timeout.Token).ConfigureAwait(false);

                GameWatchVision.Clue clue = GameWatchVision.Parse(answer ?? "");
                if (clue.Useful) return clue;
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "游戏陪玩：视觉分析失败");
            }
        }

        // 视觉模型没配 / 没给线索 → 用系统 OCR 至少把画面文字读出来
        if (!Configuration.UseLocalOcr) return new GameWatchVision.Clue();

        string ocr = await MediaOcr.ReadAsync(path, Configuration.OcrLanguage ?? "zh-Hans")
            .ConfigureAwait(false);

        return ocr.Length > 0
            ? new GameWatchVision.Clue { Situation = ocr, Kind = "其他" }
            : new GameWatchVision.Clue();
    }

    /// <summary>把四行线索压成一句话（给状态栏/事件行用）。</summary>
    static string BuildSummary(GameWatchVision.Clue clue)
    {
        if (!clue.Useful) return "";

        var sb = new StringBuilder();

        if (clue.Game.Length > 0) sb.Append(clue.Game).Append("｜");
        sb.Append(clue.Kind);

        if (clue.Situation.Length > 0) sb.Append("｜").Append(clue.Situation);
        if (clue.Change.Length > 0 && clue.Change != "无") sb.Append("｜变化：").Append(clue.Change);

        return sb.ToString();
    }

    async Task MaybeTalkAsync(LookResult result, bool keyEvent, bool alreadyApproved = false)
    {
        // 没分析（画面没变）就没什么可说
        if (!result.Analyzed) return;

        DateTime now = DateTime.Now;

        if (!alreadyApproved)
        {
            if (keyEvent)
            {
                // 关键节点优先说，但仍遵守静默时段 / 冷却 / 每日上限
                if ((now - lastChatAt).TotalSeconds < Math.Max(20, Configuration.MinChatIntervalSeconds / 2.0)) return;
            }
            else
            {
                string mode = (Configuration.SendMode ?? "").Trim().ToLowerInvariant();

                if (mode == "proactive")
                {
                    if (!ShouldProactive(now)) return;
                }
                else if (mode != "every")
                {
                    return;
                }
            }
        }

        if (waking) return;
        if (InQuietHours(now)) return;
        if (!functionCaller.IsIdle || ChatBot.IsChatOccupied)
        {
            // 模型正忙：先别丢，留到下一拍空闲时补一句（最多留 90 秒）
            if (!alreadyApproved)
            {
                pendingLook = result;
                pendingLookAt = now;
                pendingLookKeyEvent = keyEvent;
            }
            return;
        }
        if ((now - lastChatAt).TotalSeconds < Math.Max(30, Configuration.MinChatIntervalSeconds)) return;

        EnsureDay(now);
        if (chatCount >= Math.Max(0, Configuration.DailyLimit)) return;

        chatCount++;
        lastChatAt = now;

        var sb = new StringBuilder();
        sb.AppendLine($"[游戏陪玩·画面] 你正坐在主人旁边看他玩《{result.WindowTitle}》。");
        if (result.Game.Length > 0) sb.AppendLine($"游戏：{result.Game}");
        if (result.Kind.Length > 0) sb.AppendLine($"这一刻：{result.Kind}" + (keyEvent ? "（关键节点）" : ""));
        if (result.Situation.Length > 0) sb.AppendLine($"局面：{result.Situation}");
        if (result.Change.Length > 0 && result.Change != "无") sb.AppendLine($"变化：{result.Change}");

        // 最近几条事件（不含刚记下的这一条）：让 AI 知道剧情/战斗是怎么一步步走过来的
        List<GameEvent> recentAll = events?.Recent(4) ?? new List<GameEvent>();
        if (recentAll.Count > 1)
        {
            sb.AppendLine("刚才的经过：");
            foreach (GameEvent item in recentAll.Take(recentAll.Count - 1).OrderBy(e => e.At))
                sb.AppendLine(item.Line());
        }

        if (!result.HasClue)
            sb.AppendLine($"（视觉模型没给出线索；想看可以分析这张图：{result.Path}）");
        else
            sb.AppendLine($"图片：{result.Path}");

        sb.AppendLine(StyleHint());
        sb.AppendLine("像坐在旁边一起玩的人那样说一句：可以点评、提醒或吐槽；"
                      + "不要复述画面（「画面里有血条」这种），不要编看不到的数字；不想说就沉默。");

        _ = TalkAsync(sb.ToString().TrimEnd());
    }

    /// <summary>模型忙时暂存的画面，等到空闲再补一句；超过 90 秒就丢掉（画面已经过时）。</summary>
    bool TryFlushPendingTalk()
    {
        LookResult? pending = pendingLook;
        if (pending == null) return false;

        DateTime now = DateTime.Now;
        if ((now - pendingLookAt).TotalSeconds > 90)
        {
            pendingLook = null;
            return false;
        }
        if (InQuietHours(now)) return false;
        if (!functionCaller.IsIdle || ChatBot.IsChatOccupied) return false;
        if ((now - lastChatAt).TotalSeconds < Math.Max(30, Configuration.MinChatIntervalSeconds)) return false;

        pendingLook = null;
        _ = MaybeTalkAsync(pending, pendingLookKeyEvent, alreadyApproved: true);
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // 赛前功课：第一次陪玩某款游戏时先让 AI 了解它，之后每次陪玩都带上
    // ─────────────────────────────────────────────────────────────

    /// <summary>换游戏/第一次看到窗口时调用：有功课就注入，没有就（空闲时）布置一次功课。</summary>
    void EnsureKnowledge(string title)
    {
        string key = CleanGameName(title);
        if (key.Length == 0) return;

        if (activeTitleKey != key)
        {
            activeTitleKey = key;
            GameKnowledge? found = knowledge?.Find(key, title, lastGame);
            activeKnowledge = found;
            if (found != null) UpsertKnowledgeNote(found);
        }

        if (activeKnowledge != null || !Configuration.PreGameStudy) return;
        if (lastStudyGame == key && (DateTime.Now - lastStudyAt).TotalHours < 2) return;
        if (!functionCaller.IsIdle || ChatBot.IsChatOccupied) return;

        lastStudyGame = key;
        lastStudyAt = DateTime.Now;
        _ = StudyGameAsync(key);
    }

    /// <summary>让 AI 先做功课，并用 game_watch(learn=...) 把笔记存下来。</summary>
    async Task StudyGameAsync(string game)
    {
        try
        {
            string note = "[游戏陪玩·赛前功课]\n"
                          + $"你即将陪主人玩《{game}》，但本地还没有它的功课。\n"
                          + "先用你可用的搜索/资料工具了解：游戏类型、核心玩法、剧情背景与主要角色、常见术语、"
                          + "典型事件节点（加载/菜单/战斗/剧情/结算/胜负/升级/死亡）和战斗机制。\n"
                          + "查不到资料就用自己的知识，别编。\n"
                          + $"然后用 game_watch(learn=\"...\", game=\"{game}\") 把 300 字以内的功课存下来；最后只回一句「功课做好了」。";
            await ChatBot.ChatAsync(new ChatMessageContent(AuthorRole.System, note), true).ConfigureAwait(false);
            logger.LogInformation("游戏陪玩：已为《{Game}》布置赛前功课", game);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "游戏陪玩：赛前功课失败");
        }
    }

    /// <summary>把功课写进 System 上下文：同 tag 只留最新一条，聊天窗口不显示。</summary>
    void UpsertKnowledgeNote(GameKnowledge item)
    {
        string tag = "[游戏陪玩·功课]";
        string body = TrimText(item.Notes, Math.Clamp(Configuration.KnowledgeMaxChars, 100, 3000));
        try
        {
            ChatBot.EditChatHistory(thread =>
            {
                var history = thread.ChatHistory;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    if (history[i].Role != AuthorRole.System) continue;
                    if ((history[i].Content ?? "").StartsWith(tag + "\n", StringComparison.Ordinal))
                        history.RemoveAt(i);
                }
                history.AddSystemMessage(tag + "\n游戏：" + item.Game + "\n" + body);
            }, "游戏陪玩·功课注入");
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "游戏陪玩：功课注入失败");
        }
    }

    /// <summary>窗口标题通常带「- 平台/版本」尾巴，取主段当游戏名。</summary>
    static string CleanGameName(string title)
    {
        string text = (title ?? "").Trim();
        if (text.Length == 0) return "";
        foreach (string sep in new[] { " - ", " — ", " | ", "（", "(" })
        {
            int index = text.IndexOf(sep, StringComparison.Ordinal);
            if (index > 2)
            {
                text = text[..index].Trim();
                break;
            }
        }
        return text.Length > 40 ? text[..40] : text;
    }

    static string TrimText(string text, int max)
    {
        string value = (text ?? "").Trim();
        if (max <= 0 || value.Length <= max) return value;
        return value[..max] + "…";
    }

    /// <summary>陪玩风格：只影响语气，不影响识图。</summary>
    string StyleHint() => (Configuration.Style ?? "默认").Trim() switch
    {
        "热情鼓励" => "语气热情，多鼓励；主人失误也别嘲讽，给点正反馈。",
        "损友调侃" => "像老朋友一样损两句，但不能伤人；点到为止，别一直阴阳。",
        "专业解说" => "像解说一样讲清局势和下一步建议，简短有力，不废话。",
        "温柔陪伴" => "语气温柔，轻声提醒，别打扰操作节奏。",
        _ => "语气自然，像坐旁边一起玩的朋友。"
    };

    bool ShouldProactive(DateTime now)
    {
        int minutes = Math.Max(1, Configuration.ProactiveEveryMinutes);
        if ((now - lastProactiveAt).TotalMinutes < minutes) return false;
        lastProactiveAt = now;
        return Random.Shared.NextDouble() < Math.Clamp(Configuration.ProactiveChance, 0.0, 1.0);
    }

    async Task TalkAsync(string note)
    {
        if (waking) return;
        waking = true;
        try
        {
            ILanguageModel? model = languageModel ?? ChatBot.LanguageModel;
            OccupationMarker? marker = null;
            if (model != null)
            {
                try { marker = model.GetThinkingRequester().Rent("游戏陪玩"); } catch { }
            }
            try
            {
                await interactor.ChatAsync(note).ConfigureAwait(false);
            }
            finally
            {
                try { marker?.Dispose(); } catch { }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "游戏陪玩：主动说话失败");
        }
        finally
        {
            waking = false;
        }
    }

    void EnsureDay(DateTime now)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (!string.Equals(dayKey, today, StringComparison.Ordinal))
        {
            dayKey = today;
            chatCount = 0;
        }
    }

    bool InQuietHours(DateTime now)
    {
        string value = (Configuration.QuietHours ?? "").Trim();
        if (value.Length == 0) return false;
        string[] parts = value.Split(new[] { '-', '~', '至' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!TimeSpan.TryParse(parts[0].Trim(), out TimeSpan start)) return false;
        if (!TimeSpan.TryParse(parts[1].Trim(), out TimeSpan end)) return false;
        if (start == end) return false;
        TimeSpan time = now.TimeOfDay;
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    // ─────────────────────────────────────────────────────────────
    // AI 工具
    // ─────────────────────────────────────────────────────────────

    [XmlFunction(FunctionMode.OneShot, "game_watch")]
    [Description("游戏陪玩：按间隔截取游戏窗口，先本地比对变化，画面真变了才交给视觉模型抽取线索，按策略主动说一句。statusOnly 看状态；look=true 立即看一眼（强制分析）；timeline=true 看最近事件；recap=true 复盘最近 N 分钟；pause/resume 暂停/继续；listWindows 列出可截窗口；window=关键字 临时指定游戏；preset=预设名 切换游戏类型；learn=功课正文 保存赛前功课；showKnowledge=true 查看功课；study=true 让 AI 去了解当前游戏并生成功课。")]
    public async Task<string> GameWatch(
        [Description("只看状态")] bool? statusOnly = null,
        [Description("true=现在看一眼（强制分析，不主动叫醒模型）")] bool? look = null,
        [Description("true=暂停自动截图")] bool? pause = null,
        [Description("true=继续自动截图")] bool? resume = null,
        [Description("true=列出当前可截窗口")] bool? listWindows = null,
        [Description("临时设置游戏窗口标题关键字；空=跟随前台")] string? window = null,
        [Description("true=最近事件时间轴")] bool? timeline = null,
        [Description("true=复盘最近 N 分钟（配合 minutes）")] bool? recap = null,
        [Description("复盘/时间轴覆盖的分钟数，默认 30")] int? minutes = null,
        [Description("切换游戏类型预设：通用（推荐）/ FPS 竞技 / MOBA 团战 / RPG·剧情 / 音游 / 休闲建造")] string? preset = null,
        [Description("赛前功课正文：调研完这款游戏后把 300 字以内的笔记传进来保存")] string? learn = null,
        [Description("learn 对应的游戏名；留空=用当前窗口标题")] string? gameName = null,
        [Description("true=查看当前游戏的赛前功课")] bool? showKnowledge = null,
        [Description("true=让 AI 现在去了解当前游戏并生成功课")] bool? study = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(learn))
            {
                string key = !string.IsNullOrWhiteSpace(gameName)
                    ? gameName!.Trim()
                    : CleanGameName(lastWindowTitle);
                if (key.Length == 0) key = CleanGameName(Configuration.WindowKeyword);
                if (key.Length == 0) key = "未命名游戏";
                if (knowledge == null) return "[游戏陪玩] 功课库还没初始化，稍后再试。";

                GameKnowledge saved = knowledge.Save(key, learn!);
                activeKnowledge = saved;
                activeTitleKey = key;
                UpsertKnowledgeNote(saved);
                return $"[游戏陪玩] 已记下《{saved.Game}》的赛前功课（{saved.Notes.Length} 字），陪玩时会自动带上。";
            }

            if (showKnowledge == true)
            {
                GameKnowledge? found = activeKnowledge
                                       ?? knowledge?.Find(CleanGameName(lastWindowTitle), lastWindowTitle, lastGame);
                return found == null
                    ? "[游戏陪玩] 还没有这款游戏的功课。可以让 AI 用 game_watch(study=true) 去做一次。"
                    : $"[游戏陪玩·功课] 《{found.Game}》（{found.UpdatedAt:yyyy-MM-dd HH:mm}）\n{found.Notes}";
            }

            if (study == true)
            {
                string game = CleanGameName(lastWindowTitle);
                if (game.Length == 0) game = CleanGameName(Configuration.WindowKeyword);
                if (game.Length == 0) game = "当前游戏";
                lastStudyGame = "";
                _ = StudyGameAsync(game);
                return $"[游戏陪玩] 已让 AI 去做《{game}》的赛前功课，做完会存进笔记。";
            }

            if (!Configuration.Enable)
                return "[游戏陪玩] 插件已停用（控制台「启用游戏陪玩」）。";

            if (pause == true)
            {
                paused = true;
                GameWatchCapture.ReleaseCapture();
                return "[游戏陪玩] 已暂停自动截图。";
            }
            if (resume == true)
            {
                paused = false;
                lastCaptureAt = DateTime.Now;
                return "[游戏陪玩] 已继续自动截图。";
            }
            if (listWindows == true)
                return BuildWindowList();

            if (!string.IsNullOrWhiteSpace(preset))
                return ApplyPreset(preset);

            if (!string.IsNullOrWhiteSpace(window))
            {
                Configuration.WindowKeyword = window.Trim();
                return $"[游戏陪玩] 本次目标窗口改为标题包含「{Configuration.WindowKeyword}」。长期修改请到控制台。";
            }

            if (timeline == true)
            {
                List<GameEvent> recent = events?.Recent(15) ?? new List<GameEvent>();
                if (recent.Count == 0) return "[游戏陪玩·时间轴] 还没有观察到画面。";

                var sb = new StringBuilder("[游戏陪玩·时间轴] 最近 " + recent.Count + " 条：\n");
                foreach (GameEvent item in recent) sb.AppendLine(item.Line(true));
                return sb.ToString().TrimEnd();
            }

            if (recap == true)
            {
                int span = Math.Clamp(minutes ?? 30, 1, 600);
                return "[游戏陪玩·复盘]\n" + (events?.Recap(span) ?? "事件库没初始化。");
            }

            if (statusOnly == true) return BuildStatus();

            if (look == true)
            {
                LookResult result = await LookOnceAsync().ConfigureAwait(false);
                if (!result.Ok) return "[游戏陪玩] 看一眼失败：" + result.Error;

                var sb = new StringBuilder();
                sb.AppendLine($"[游戏陪玩] 已看一眼《{result.WindowTitle}》（{result.DiffText} {result.DiffScore:0.#}）。");

                if (!result.Analyzed)
                    sb.AppendLine("画面没变化，这次没调视觉模型。");
                else if (result.HasClue)
                {
                    if (result.Game.Length > 0) sb.AppendLine("游戏：" + result.Game);
                    sb.AppendLine("这一刻：" + result.Kind);
                    if (result.Situation.Length > 0) sb.AppendLine("局面：" + result.Situation);
                    if (result.Change.Length > 0 && result.Change != "无") sb.AppendLine("变化：" + result.Change);
                }
                else
                    sb.AppendLine("（视觉模型没给出线索；可以把这张图交给视觉工具细看）");

                sb.AppendLine("图片：" + result.Path);
                if (result.Keyframe.Length > 0) sb.AppendLine("关键帧：" + result.Keyframe);

                return sb.ToString().TrimEnd();
            }

            return BuildStatus();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "游戏陪玩：工具执行失败");
            return "[游戏陪玩] 执行失败：" + e.Message;
        }
    }

    /// <summary>套用游戏预设（面板与工具共用）。</summary>
    public string ApplyPreset(string name)
    {
        GameWatchPresets.Preset? picked = GameWatchPresets.Find((name ?? "").Trim());

        if (picked == null)
            return $"[游戏陪玩] 没有这个预设：{name}。可选："
                   + string.Join(" / ", GameWatchPresets.All.Select(p => p.Name));

        GameWatchPresets.Apply(picked, Configuration);
        lastCaptureAt = DateTime.Now;   // 切换后先安静一个间隔

        return $"[游戏陪玩] 已切到「{picked.Name}」：每 {picked.IntervalSeconds} 秒一帧、"
               + $"变化阈值 {picked.DiffThreshold}、关键帧阈值 {picked.KeyframeThreshold}、宽 {picked.Width}。"
               + $"\n关注点：{picked.Focus}";
    }

    public string UiApplyPreset(string name) => ApplyPreset(name);

    /// <summary>面板用：最近事件行。</summary>
    public List<GameEvent> UiRecentEvents(int count) => events?.Recent(count) ?? new List<GameEvent>();

    string BuildStatus()
    {
        var sb = new StringBuilder();
        sb.Append("[游戏陪玩]\n");
        sb.Append($"开关：{(Configuration.Enable ? "开" : "关")} · {(paused ? "暂停" : "运行")} · 预设：{UiPreset}\n");
        sb.Append($"目标：{(string.IsNullOrWhiteSpace(Configuration.WindowKeyword) ? "跟随前台（排除浏览器/聊天/密码窗口）" : "标题包含「" + Configuration.WindowKeyword.Trim() + "」")}\n");
        sb.Append($"节奏：每 {Math.Max(5, Configuration.CaptureIntervalSeconds)} 秒抓一帧 · 变化阈值 {Configuration.DiffThreshold:0.#} · 关键帧阈值 {Configuration.KeyframeThreshold:0.#}\n");
        sb.Append($"说话：{(Configuration.SendMode ?? "").Trim()} · 风格 {Configuration.Style} · 今日 {chatCount}/{Math.Max(0, Configuration.DailyLimit)}\n");
        sb.Append($"上次窗口：{(lastWindowTitle.Length > 0 ? lastWindowTitle : "还没截过")}\n");

        if (lastDiffText.Length > 0)
            sb.Append($"上次变化：{lastDiffText}（{lastDiff:0.#}）" + (lastKind.Length > 0 ? $" · {lastKind}" : "") + "\n");

        if (lastFramePath.Length > 0)
            sb.Append($"上次截图：{lastFramePath}\n");
        if (lastSummary.Length > 0)
            sb.Append($"最近摘要：{Shorten(lastSummary, 200)}\n");

        sb.Append($"事件时间轴：{events?.Count ?? 0} 条（保留 {Math.Max(20, Configuration.KeepEvents)}）· 截图缓存 {GameWatchCapture.CountFrames(framesDir)} 张\n");
        string knowledgeName = activeKnowledge?.Game
                               ?? knowledge?.Find(CleanGameName(lastWindowTitle), lastWindowTitle, lastGame)?.Game
                               ?? "";
        sb.Append($"赛前功课：{(knowledgeName.Length > 0 ? knowledgeName : "无")} · 共 {knowledge?.Count ?? 0} 份（knowledge.json）\n");
        if (lastError.Length > 0)
            sb.Append($"错误：{lastError}\n");
        return sb.ToString().TrimEnd();
    }

    string BuildWindowList()
    {
        List<WindowEntry> windows = GameWatchCapture.ListWindows();
        var sb = new StringBuilder();
        sb.Append("[可截窗口]\n");
        if (windows.Count == 0) sb.Append("（没有枚举到可见窗口）");
        foreach (WindowEntry window in windows.Take(40))
        {
            string excluded = GameWindowMatcher.IsExcluded(window.Title, Configuration.ExcludeKeywords) ? "（排除）" : "";
            sb.Append($"- {window.Title}（hwnd {window.Handle}）{excluded}\n");
        }
        return sb.ToString().TrimEnd();
    }

    static string Shorten(string? text, int max)
    {
        string value = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    // ─────────────────────────────────────────────────────────────
    // 面板动作
    // ─────────────────────────────────────────────────────────────

    public string UiStatusText() => BuildStatus();

    public string UiSaveConfig()
    {
        try
        {
            string path = Path.Combine(AlifePath.StorageFolderPath, "Configuration", typeof(GameWatchModule).FullName + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Configuration, new JsonSerializerOptions { WriteIndented = true }));
            return "配置已保存：" + path;
        }
        catch (Exception e)
        {
            return "保存配置失败：" + e.Message;
        }
    }

    public string UiToggleEnable()
    {
        Configuration.Enable = !Configuration.Enable;
        if (!Configuration.Enable) paused = false;
        UiSaveConfig();
        return Configuration.Enable ? "已启用游戏陪玩（已写盘）。" : "已停用游戏陪玩（已写盘）。";
    }

    public string UiTogglePause()
    {
        paused = !paused;
        if (!paused) lastCaptureAt = DateTime.Now;
        return paused ? "已暂停自动截图。" : "已继续自动截图。";
    }

    public async Task<string> UiLookNowAsync()
    {
        if (!Configuration.Enable) return "插件已停用，先到配置里开启「启用游戏陪玩」。";
        LookResult result = await LookOnceAsync().ConfigureAwait(false);
        if (!result.Ok) return "看一眼失败：" + result.Error;
        string summary = string.IsNullOrWhiteSpace(result.Summary) ? "（视觉模型没接上）" : result.Summary;
        return $"已看一眼《{result.WindowTitle}》：\n{summary}\n截图：{result.Path}";
    }

    public string UiListWindows() => BuildWindowList();

    public string UiClearFrames()
    {
        try
        {
            foreach (string file in Directory.GetFiles(framesDir, "*.jpg"))
                File.Delete(file);
            return "已清空截图缓存。";
        }
        catch (Exception e)
        {
            return "清空失败：" + e.Message;
        }
    }

    public string UiOpenFramesFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(framesDir) { UseShellExecute = true });
            return "已打开截图目录：" + framesDir;
        }
        catch (Exception e)
        {
            return "打开失败：" + e.Message;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 工具结果回注（Alife 的 XML 函数会丢弃返回值）
    // ─────────────────────────────────────────────────────────────

    void WrapHandlerResultsForPoke(XmlHandler handler)
    {
        foreach (XmlFunction fn in handler.Functions)
        {
            MethodInfo? method = GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    XmlFunctionAttribute? attr = m.GetCustomAttribute<XmlFunctionAttribute>();
                    return attr != null && (attr.Name ?? m.Name).Equals(fn.Name, StringComparison.OrdinalIgnoreCase);
                });
            if (method == null) continue;

            ParameterInfo[] parameters = method.GetParameters();
            fn.Invoker = async (XmlContext ctx, CancellationToken ct) =>
            {
                if (ctx.CallMode != CallMode.OneShot)
                    throw new Exception($"调用 {fn.Name} 标签的方式错误，应该使用单个自闭合标签调用。");

                object?[] args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    ParameterInfo p = parameters[i];
                    if (p.ParameterType == typeof(CancellationToken)) { args[i] = ct; continue; }
                    if (typeof(XmlContext).IsAssignableFrom(p.ParameterType)) { args[i] = ctx; continue; }
                    if (p.GetCustomAttribute<XmlContentAttribute>() != null) { args[i] = ctx.Content; continue; }

                    object? value = null;
                    bool filled = false;
                    if (p.Name != null && ctx.Parameters.TryGetValue(p.Name, out string? raw))
                    {
                        TypeConverter converter = TypeDescriptor.GetConverter(p.ParameterType);
                        if (converter.CanConvertFrom(typeof(string)))
                        {
                            try
                            {
                                value = converter.ConvertFromInvariantString(raw);
                                filled = true;
                            }
                            catch { }
                        }
                    }
                    if (!filled && p.HasDefaultValue)
                    {
                        value = p.DefaultValue;
                        filled = true;
                    }
                    if (!filled)
                        throw new Exception($"<{fn.Name}> 缺少 {p.Name} 参数，或参数格式不对！");
                    args[i] = value;
                }

                object? back = method.Invoke(this, args);
                string? text = null;
                if (back is Task<string> taskOfString) text = await taskOfString.ConfigureAwait(false);
                else if (back is Task task) await task.ConfigureAwait(false);
                else if (back is string plain) text = plain;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { interactor.Poke(Compact(text)); } catch { }
                }
            };
        }
    }

    string Compact(string text)
    {
        int max = Math.Clamp(Configuration.PokeMaxChars, 500, 10000);
        if (text.Length <= max) return text;
        int head = (int)(max * 0.8);
        int tail = Math.Max(40, max - head - 24);
        return text[..head] + "\n…（已截断）\n" + text[^tail..];
    }
}
