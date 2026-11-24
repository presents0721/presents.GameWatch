using System;
using System.ComponentModel;

namespace presents.GameWatch;

public class GameWatchConfig
{
    [DisplayName("启用游戏陪玩")]
    [Description("默认关。开启后才会截取游戏窗口；关闭立即停止。")]
    public bool Enable { get; set; } = false;

    [DisplayName("游戏窗口关键字")]
    [Description("窗口标题包含这个关键字就截它（如「原神」「Minecraft」）；留空=跟随当前前台窗口。")]
    public string WindowKeyword { get; set; } = "";

    [DisplayName("游戏类型预设")]
    [Description("通用 / FPS 竞技 / MOBA 团战 / RPG·剧情 / 音游 / 休闲建造。套用后自动设好抓帧节奏、变化阈值、截图尺寸和关注点，仍可单项微调。")]
    public string Preset { get; set; } = "通用（推荐）";

    [DisplayName("截图间隔（秒）")]
    [Description("隔多久抓一帧，默认 6 秒。抓帧本身不花 token；只有画面真的变了才会调视觉模型。")]
    public int CaptureIntervalSeconds { get; set; } = 6;

    [DisplayName("截图方式")]
    [Description("自动（推荐）：优先 PrintWindow（不建截图会话、不会闪边框），截不到再回退 WGC；\nWGC：兼容性最好，但 Win11 每次截图会短暂显示截图边框；\nPrintWindow：强制只走 PrintWindow，失败只报错不回退。")]
    public string CaptureMethod { get; set; } = "自动（推荐）";

    [DisplayName("变化阈值（0–100）")]
    [Description("缩成 96×54 灰度比对，平均差超过这个值才算「画面变了」并调用视觉模型。默认 6；调高更省 token，调低更敏感。")]
    public double DiffThreshold { get; set; } = 6;

    [DisplayName("关键帧阈值（0–100）")]
    [Description("变化超过这个值就另存一张高清关键帧（1920 宽），供模型细看或事后回看。默认 25。")]
    public double KeyframeThreshold { get; set; } = 25;

    // ---------------------------------------------------------------- 时序拼图

    // ---------------------------------------------------------------- 局面轨迹

    [DisplayName("局面转折时优先开口")]
    [Description("局势类别发生变化时（探索→遭遇战、战斗→撤退、存活→死亡），即使画面像素变化不大也当作关键节点，抢先说一句。默认开。")]
    public bool TalkOnTurn { get; set; } = true;

    [DisplayName("启用时序拼图")]
    [Description("打斗这类画面剧烈变化时，把最近几帧拼成一张图一起交给视觉模型，让它看到动作过程而不是单个瞬间。默认开。")]
    public bool EnableMontage { get; set; } = true;

    [DisplayName("拼图触发阈值（0–100）")]
    [Description("帧差异超过这个值才拼图。要比「变化阈值」高——菜单切换、加载进度这类小变化不该拼。默认 12。")]
    public double MontageMinDiff { get; set; } = 12;

    [DisplayName("拼图帧数")]
    [Description("最多拼几帧。4~6 张比较平衡，太多每格会糊。默认 6。")]
    public int MontageFrames { get; set; } = 6;

    [DisplayName("拼图时间窗口（秒）")]
    [Description("只取最近这么多秒内的帧。打斗节奏快，8~12 秒足够。默认 10。")]
    public int MontageWindowSeconds { get; set; } = 10;

    [DisplayName("拼图列数")]
    [Description("2 列最平衡；3 列更宽，适合横向位移明显的动作。默认 2。")]
    public int MontageColumns { get; set; } = 2;

    [DisplayName("每格宽度（像素）")]
    [Description("每张小图的宽度。480 在多数视觉模型上够看清动作，再大只浪费 token。默认 480。")]
    public int MontageCellWidth { get; set; } = 480;

    [DisplayName("关键帧宽度")]
    [Description("关键帧的保存宽度，默认 1920（比概览帧清楚，能看清文字与数字）。")]
    public int KeyframeWidth { get; set; } = 1920;

    [DisplayName("静止时也要看一眼（分钟）")]
    [Description("画面长时间没变化时，每隔这么久仍然分析一次（防止「挂机/加载」被永久忽略）。默认 8 分钟；0=永不看。")]
    public int StaticAnalyzeMinutes { get; set; } = 8;

    [DisplayName("关注点")]
    [Description("告诉视觉模型该看什么（预设会填）。例如 FPS 看血量/敌人/比分，RPG 看剧情/任务/Boss 阶段。")]
    public string Focus { get; set; } =
        "玩家在做什么、当前目标、明显的胜负或推进";

    [DisplayName("赛前功课")]
    [Description("开（默认）：第一次陪玩某款游戏时，先让 AI 了解它（类型/玩法/剧情背景/术语/事件节点），存成笔记；之后每次陪玩都会带上这份功课。")]
    public bool PreGameStudy { get; set; } = true;

    [DisplayName("功课注入上限（字）")]
    [Description("每次陪玩带多少字的游戏笔记，默认 600；太长会占上下文。")]
    public int KnowledgeMaxChars { get; set; } = 600;

    [DisplayName("视觉识图超时（秒）")]
    [Description("一次识图最多等多久，超时立刻退回本机 OCR（更快但只读文字）。默认 10 秒；嫌慢可以调到 5。")]
    public int VisionTimeoutSeconds { get; set; } = 10;

    [DisplayName("陪玩风格")]
    [Description("默认 / 热情鼓励 / 损友调侃 / 专业解说 / 温柔陪伴。影响说话语气，不影响识图。")]
    public string Style { get; set; } = "默认";

    [DisplayName("保留事件条数")]
    [Description("事件时间轴最多存多少条（默认 200）。存的是摘要与图片路径，不存像素；持久化到 PluginData。")]
    public int KeepEvents { get; set; } = 200;

    [DisplayName("说话方式")]
    [Description("every=每张截图都看并说话；proactive=按间隔掷骰子，中了才看并说话。默认 proactive。")]
    public string SendMode { get; set; } = "proactive";

    [DisplayName("主动看一眼间隔（分钟）")]
    [Description("proactive 模式下多久掷一次骰子，默认 5 分钟。")]
    public int ProactiveEveryMinutes { get; set; } = 5;

    [DisplayName("主动看一眼概率")]
    [Description("掷骰子的命中概率，默认 0.35。骰子本身不花 token。")]
    public double ProactiveChance { get; set; } = 0.35;

    [DisplayName("每日主动说话上限")]
    [Description("一天最多主动说几次，默认 24。到上限只截图不叫醒模型。")]
    public int DailyLimit { get; set; } = 24;

    [DisplayName("两次说话最小间隔（秒）")]
    [Description("两次主动说话至少隔多久，默认 90 秒，避免连珠炮。")]
    public int MinChatIntervalSeconds { get; set; } = 90;

    [DisplayName("静默时段")]
    [Description("如 23:00-08:00；这段时间不截图、不说话。留空=不静默。")]
    public string QuietHours { get; set; } = "";

    [DisplayName("排除窗口关键字")]
    [Description("窗口标题包含任一关键字就不截。默认排除 Alife/浏览器/聊天/密码/银行等窗口；逗号分隔。")]
    public string ExcludeKeywords { get; set; } =
        "Alife,Trae,Chrome,Edge,Firefox,浏览器,QQ,微信,Discord,Telegram,聊天,密码,银行,支付宝,任务管理器,Visual Studio,PowerShell,Terminal";

    [DisplayName("Alife 在前台时不截")]
    [Description("默认开：当前前台是 Alife/编辑器窗口时跳过，避免截到自己。")]
    public bool SkipWhenHostFocused { get; set; } = true;

    [DisplayName("保留截图张数")]
    [Description("只留最近这么多张，默认 20；0=不限。截图在 PluginData/presents.GameWatch/frames。")]
    public int MaxFrames { get; set; } = 20;

    [DisplayName("截图宽度")]
    [Description("缩到这么宽再存，默认 1280；高度按比例。")]
    public int MaxWidth { get; set; } = 1280;

    [DisplayName("JPEG 质量")]
    [Description("默认 75。越高越清楚也越大。")]
    public int JpegQuality { get; set; } = 75;

    [DisplayName("离线 OCR 兜底")]
    [Description("开（默认）：视觉模型没配或没返回内容时，用 Windows 自带 OCR 读画面上的文字（血量/比分/任务/台词）。只读文字不理解画面，但零依赖、不出网。")]
    public bool UseLocalOcr { get; set; } = true;

    [DisplayName("OCR 语言")]
    [Description("默认 zh-Hans；英文游戏用 en-US。系统没装对应语言包会自动退回用户默认语言。")]
    public string OcrLanguage { get; set; } = "zh-Hans";

    [DisplayName("视觉回复长度")]
    [Description("视觉模型最多回多少 token，默认 160（结构化四行够用，太多会开始编）。")]
    public int VisionMaxTokens { get; set; } = 160;

    [DisplayName("工具结果回注上限（字）")]
    [Description("Alife 的 XML 函数会丢弃返回值，插件需要把结果 Poke 回对话。这里限制单条最大字数，默认 3000。")]
    public int PokeMaxChars { get; set; } = 3000;
}
