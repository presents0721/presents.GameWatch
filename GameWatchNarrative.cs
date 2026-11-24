using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace presents.GameWatch;

/// <summary>
/// 局面轨迹：记住最近几轮看到了什么。
/// 
/// 单帧只能回答「现在是什么样」，但陪玩真正该说的是「比刚才怎么样了」。
/// 这个类只存不做判断 —— 趋势交给视觉模型，因为它看得见画面，比按关键词猜准。
/// </summary>
internal sealed class GameWatchNarrative
{
    readonly object gate = new();
    readonly List<GameWatchVision.Clue> entries = new();

    const int Capacity = 24;

    public void Add(GameWatchVision.Clue clue)
    {
        // Clue.Useful 只看 Game/Situation/Change，不含 Kind。
        // 但「这轮只认出了类别」恰恰是转折最需要的那条记录，不能因为描述为空就丢掉。
        if (clue == null) return;
        if (!clue.Useful && clue.Kind.Length == 0) return;

        lock (gate)
        {
            entries.Add(clue);
            if (entries.Count > Capacity)
                entries.RemoveRange(0, entries.Count - Capacity);
        }
    }

    public int Count
    {
        get { lock (gate) return entries.Count; }
    }

    /// <summary>最近 count 轮的压缩轨迹，直接放进提示词。</summary>
    public string Trace(int count = 4)
    {
        List<GameWatchVision.Clue> recent;
        lock (gate)
        {
            if (entries.Count == 0) return "";
            recent = entries.Skip(Math.Max(0, entries.Count - count)).ToList();
        }

        StringBuilder sb = new();

        for (int i = 0; i < recent.Count; i++)
        {
            GameWatchVision.Clue c = recent[i];
            string step = (i + 1).ToString();

            string head = c.Kind.Length > 0 ? c.Kind : "未知";
            string body = c.Situation.Length > 0 ? c.Situation : c.Change;

            if (body.Length > 60) body = body[..60] + "…";
            sb.AppendLine($"{step}. [{head}] {body}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 最近两轮的 Kind 是否不同 —— 变了往往意味着开打、撤退、死亡这类转折，
    /// 这种时候值得抢先说话，而不是等下一次例行观察。
    /// </summary>
    public bool JustTurned()
    {
        lock (gate)
        {
            if (entries.Count < 2) return false;

            string now = entries[^1].Kind.Trim();
            string before = entries[^2].Kind.Trim();

            if (now.Length == 0 || before.Length == 0) return false;
            return !string.Equals(now, before, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>最近一轮的局面描述，用来判断「是不是还停在原地」。</summary>
    public string LastSituation()
    {
        lock (gate) return entries.Count == 0 ? "" : entries[^1].Situation;
    }

    /// <summary>
    /// 连续几轮局面几乎没变。这种时候再说一遍同样的观察就是复读，
    /// 调用方应该主动收着点，或者干脆换个话题。
    /// </summary>
    public bool Stagnant(int threshold = 3)
    {
        lock (gate)
        {
            if (entries.Count < threshold) return false;

            List<GameWatchVision.Clue> tail = entries.Skip(entries.Count - threshold).ToList();
            string first = Normalize(tail[0].Situation);

            if (first.Length == 0) return false;

            return tail.All(x => Similarity(first, Normalize(x.Situation)) >= 0.8);
        }
    }

    static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        StringBuilder sb = new(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
                sb.Append(c);

        return sb.ToString();
    }

    /// <summary>很粗的相似度：公共前缀/后缀占比。够用来判断「几乎一模一样」。</summary>
    static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;

        int min = Math.Min(a.Length, b.Length);
        int same = 0;

        for (int i = 0; i < min; i++)
        {
            if (a[i] == b[i]) same++;
            else break;
        }

        // 前缀相同，或长度相近且前半段一致，都算像
        double prefix = same / (double)Math.Max(a.Length, b.Length);
        if (prefix >= 0.8) return prefix;

        // 再比一次结尾，覆盖「换了个说法但说的是同一件事」的部分情况
        int tail = 0;
        for (int i = 1; i <= min; i++)
        {
            if (a[^i] == b[^i]) tail++;
            else break;
        }

        return Math.Max(prefix, tail / (double)Math.Max(a.Length, b.Length));
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
    }
}
