using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace presents.GameWatch;

/// <summary>一款游戏的赛前功课：游戏名 + AI 整理的笔记（类型/玩法/剧情/术语/事件节点）。</summary>
public sealed class GameKnowledge
{
    public string Game { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 赛前功课的本地存储：PluginData/presents.GameWatch/knowledge.json。
/// 键是游戏名（通常取窗口标题的主段），查找时再做一次包含匹配，防止标题带版本号/平台后缀。
/// </summary>
public sealed class GameKnowledgeStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    readonly Dictionary<string, GameKnowledge> map = new(StringComparer.OrdinalIgnoreCase);

    public GameKnowledgeStore(string dataDir)
    {
        FilePath = Path.Combine(dataDir, "knowledge.json");
        try
        {
            if (!File.Exists(FilePath)) return;
            Dictionary<string, GameKnowledge>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, GameKnowledge>>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded == null) return;
            foreach (KeyValuePair<string, GameKnowledge> kv in loaded)
            {
                if (kv.Key.Length > 0 && kv.Value != null)
                    map[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // 文件坏了就当没有，下次保存会重建
        }
    }

    public string FilePath { get; }
    public int Count => map.Count;

    /// <summary>按名字找功课：先精确匹配，再包含匹配（窗口标题往往带「- 平台/版本」后缀）。</summary>
    public GameKnowledge? Find(params string?[] names)
    {
        foreach (string? name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string key = name.Trim();
            if (map.TryGetValue(key, out GameKnowledge? exact)) return exact;
        }

        foreach (string? name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string key = name.Trim();
            foreach (KeyValuePair<string, GameKnowledge> kv in map)
            {
                if (key.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }

        return null;
    }

    public GameKnowledge Save(string game, string notes)
    {
        string key = (game ?? "").Trim();
        if (key.Length == 0) key = "未命名游戏";
        var item = new GameKnowledge
        {
            Game = key,
            Notes = (notes ?? "").Trim(),
            UpdatedAt = DateTime.Now
        };
        map[key] = item;
        Persist();
        return item;
    }

    void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, JsonOptions));
        }
        catch
        {
            // 写盘失败不影响本次会话
        }
    }
}
