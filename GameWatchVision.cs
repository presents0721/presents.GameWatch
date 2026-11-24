using System;
using System.Collections.Generic;
using System.Text;

namespace presents.GameWatch;

/// <summary>
/// 视觉分析的「结构化抽取」层。
///
/// 借鉴 dsh-game-pet 的关键教训：识图和说话必须拆两步。
/// 如果直接问「描述画面里在发生什么」，视觉模型会把界面逐项翻译一遍
/// （"画面里有血条和敌人"）——那不是陪玩，是念屏。
/// 所以这里只要四行结构化线索，由主模型结合游戏常识再组织语言。
/// </summary>
public static class GameWatchVision
{
    public static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "加载", "菜单", "战斗", "剧情", "结算", "胜利", "失败", "升级", "死亡", "其他"
    };

    public sealed class Clue
    {
        public string Game { get; init; } = "";
        public string Situation { get; init; } = "";
        public string Change { get; init; } = "";
        public string Kind { get; init; } = "";

        public bool Useful => Game.Length > 0 || Situation.Length > 0 || Change.Length > 0;
    }

    /// <summary>
    /// 构造抽取提示词：只问四行，附上一帧线索做对比；有赛前功课时带上，帮助认人认术语。
    /// montageHint 非空时说明这是一张时间序列拼图，模型要把「变化」写成整段过程而不是逐格念画面。
    /// </summary>
    public static string BuildPrompt(
        GameWatchConfig config, string previousSituation, string previousKind,
        string? knowledge = null, string montageHint = "", string trace = "", bool stagnant = false)
    {
        string focus = string.IsNullOrWhiteSpace(config.Focus)
            ? "玩家在做什么、当前目标、明显的胜负或推进"
            : config.Focus.Trim();

        string previous = string.IsNullOrWhiteSpace(previousSituation)
            ? "（没有上一帧，这是第一次看）"
            : $"上一帧局势：{previousSituation}" + (string.IsNullOrWhiteSpace(previousKind) ? "" : $"（{previousKind}）");

        var sb = new StringBuilder();
        sb.AppendLine("你是游戏画面分析器，只做结构化抽取，不复述画面。");
        sb.AppendLine("严格按下面 4 行输出，每行「键：值」，不要标题、不要多余段落：");
        sb.AppendLine("游戏：（认得出就写游戏名；认不出写 未知）");
        sb.AppendLine("局势：（玩家/对手状态、当前目标、能看清的读数，如 血量 62%、比分 3-2；看不清就不写，不要猜）");
        sb.AppendLine("变化：（相对上一帧发生了什么；没有变化写 无）");
        sb.AppendLine("事件：（加载 / 菜单 / 战斗 / 剧情 / 结算 / 胜利 / 失败 / 升级 / 死亡 / 其他，选一个）");
        sb.AppendLine();
        sb.AppendLine("关注点：" + focus);

        if (!string.IsNullOrWhiteSpace(montageHint))
        {
            sb.AppendLine(montageHint);
            sb.AppendLine("「变化」那一行请概括这一整段里发生的动作过程（谁从哪到哪、做了什么、局面怎么变），" +
                          "不要逐格描述每张图，也不要只写最后一格的静态画面。");
        }

        sb.AppendLine(previous);

        // 最近几轮的轨迹。单帧只能回答「现在是什么样」，
        // 有了轨迹模型才能说「比刚才怎么样」—— 这才是陪玩该说的话。
        if (!string.IsNullOrWhiteSpace(trace))
        {
            sb.AppendLine("最近几轮的局面（从早到晚）：");
            sb.AppendLine(trace);
            sb.AppendLine("「变化」那一行优先写和上一轮的不同之处；如果局势基本没推进，就直说「局面没变」，" +
                          "不要换个说法重复描述同一件事。");
        }

        if (stagnant)
        {
            sb.AppendLine("注意：已经连续几轮局面几乎一样了。这一轮如果确实没新情况，可以只写「还在原地」，" +
                          "不要硬凑内容。");
        }
        if (!string.IsNullOrWhiteSpace(knowledge))
        {
            string trim = knowledge!.Trim();
            if (trim.Length > 300) trim = trim[..300] + "…";
            sb.AppendLine("已知游戏背景（用于认人/认术语，画面里不一定出现）：" + trim);
        }
        sb.AppendLine("规则：每行一句话；不要形容词堆砌；不要描述 UI 布局；不要编看不到的数字。");

        return sb.ToString().TrimEnd();
    }

    /// <summary>解析模型输出（容忍全角/半角冒号、多余前后缀）。</summary>
    public static Clue Parse(string raw)
    {
        string game = "", situation = "", change = "", kind = "";

        foreach (string line in (raw ?? "").Replace("\r", "").Split('\n'))
        {
            string text = line.Trim().TrimStart('-', '·', '*', ' ');
            if (text.Length == 0) continue;

            int split = text.IndexOfAny(new[] { '：', ':' });
            if (split <= 0 || split >= text.Length - 1) continue;

            string key = text[..split].Trim();
            string value = text[(split + 1)..].Trim().Trim('。');

            if (key.Length == 0 || value.Length == 0) continue;

            if (key.StartsWith("游戏", StringComparison.Ordinal)) game = value;
            else if (key.StartsWith("局势", StringComparison.Ordinal) || key.StartsWith("状态", StringComparison.Ordinal)) situation = value;
            else if (key.StartsWith("变化", StringComparison.Ordinal)) change = value;
            else if (key.StartsWith("事件", StringComparison.Ordinal)) kind = value;
        }

        kind = NormalizeKind(kind);

        return new Clue
        {
            Game = Shorten(game, 40),
            Situation = Shorten(situation, 160),
            Change = Shorten(change, 120),
            Kind = kind
        };
    }

    public static string NormalizeKind(string value)
    {
        string text = (value ?? "").Trim();

        if (text.Length == 0) return "其他";

        foreach (string known in Kinds)
        {
            if (text.Contains(known, StringComparison.Ordinal)) return known;
        }

        // 常见同义词归并
        if (text.Contains("战", StringComparison.Ordinal)) return "战斗";
        if (text.Contains("死", StringComparison.Ordinal) || text.Contains("阵亡", StringComparison.Ordinal)) return "死亡";
        if (text.Contains("赢", StringComparison.Ordinal) || text.Contains("胜利", StringComparison.Ordinal)) return "胜利";
        if (text.Contains("输", StringComparison.Ordinal) || text.Contains("败", StringComparison.Ordinal)) return "失败";

        return "其他";
    }

    static string Shorten(string value, int max)
    {
        string text = (value ?? "").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max];
    }
}
