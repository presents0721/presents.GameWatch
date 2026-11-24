using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace presents.GameWatch;

/// <summary>一次画面观察的结构化结果。</summary>
public sealed class GameEvent
{
    public DateTime At { get; set; }
    public string Game { get; set; } = "";
    public string Kind { get; set; } = "";        // 战斗/菜单/加载/剧情/结算/胜利/失败/升级/其他
    public string Situation { get; set; } = "";   // 局势：玩家/敌人/目标/读数
    public string Change { get; set; } = "";      // 相对上一帧的变化
    public string DiffText { get; set; } = "";    // 变化很大/有变化/几乎没变
    public double DiffScore { get; set; }
    public string Frame { get; set; } = "";       // 概览帧路径
    public string Keyframe { get; set; } = "";    // 关键帧路径（高分辨率，可空）

    [JsonIgnore] public bool IsKey => Kind is "胜利" or "失败" or "结算" or "升级" or "死亡";

    public string Line(bool withPath = false)
    {
        string time = At.ToString("HH:mm:ss");
        string head = $"· {time}｜{Kind}｜{DiffText}（{DiffScore:0.#}）";

        if (Situation.Length > 0) head += "｜" + Situation;
        if (Change.Length > 0) head += "｜变化：" + Change;
        if (withPath && Keyframe.Length > 0) head += "\n  关键帧：" + Keyframe;

        return head;
    }
}

/// <summary>
/// 事件时间轴：把每次观察压成一行结构化线索存盘，供模型回答「刚才打得怎么样」。
///
/// 只存摘要与图片路径，不存像素；默认保留 200 条，跨会话可读。
/// </summary>
public sealed class GameEventStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    readonly string path;
    readonly List<GameEvent> events = new();

    public GameEventStore(string filePath)
    {
        path = filePath;
        Load();
    }

    public int Count => events.Count;

    public IReadOnlyList<GameEvent> All => events;

    public GameEvent? Last => events.Count > 0 ? events[^1] : null;

    public void Add(GameEvent value, int keep)
    {
        events.Add(value);

        int limit = Math.Clamp(keep, 20, 2000);
        if (events.Count > limit)
            events.RemoveRange(0, events.Count - limit);

        Save();
    }

    public List<GameEvent> Recent(int count)
    {
        int take = Math.Clamp(count, 1, 50);
        return events.Skip(Math.Max(0, events.Count - take)).ToList();
    }

    public List<GameEvent> Since(DateTime from)
        => events.Where(e => e.At >= from).ToList();

    /// <summary>最近 N 分钟的复盘文本（关键事件单独标出）。</summary>
    public string Recap(int minutes, int maxLines = 30)
    {
        DateTime from = DateTime.Now.AddMinutes(-Math.Clamp(minutes, 1, 600));
        List<GameEvent> slice = Since(from);

        if (slice.Count == 0)
            return $"最近 {minutes} 分钟没有观察到画面（可能没在玩，或画面一直没变化）。";

        var sb = new StringBuilder();
        sb.AppendLine($"[最近 {minutes} 分钟｜{slice.Count} 条观察｜关键事件 {slice.Count(e => e.IsKey)} 次]");

        foreach (GameEvent item in slice.Skip(Math.Max(0, slice.Count - maxLines)))
            sb.AppendLine(item.Line());

        GameEvent? last = slice[^1];
        if (last.Keyframe.Length > 0)
            sb.AppendLine("最近关键帧：" + last.Keyframe);

        return sb.ToString().TrimEnd();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(path)) return;

            List<GameEvent>? data = JsonSerializer.Deserialize<List<GameEvent>>(File.ReadAllText(path), Json);
            if (data != null) events.AddRange(data);
        }
        catch
        {
            // 坏了就当空时间轴，不影响陪玩
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
            File.WriteAllText(path, JsonSerializer.Serialize(events, Json));
        }
        catch
        {
            // 写不进去也只是丢历史，不影响当前陪玩
        }
    }
}
