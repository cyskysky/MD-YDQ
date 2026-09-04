using System.IO;
using System.Text.Json;

namespace MDReader.Services;

/// <summary>
/// 打开历史的一条记录：目录 + 最后打开时间 + 当时文档数。
/// 对标 VS Code「文件 → 打开最近的文件」/ Typora 最近文档。
/// </summary>
public sealed class HistoryEntry
{
    public string Folder { get; set; } = string.Empty;
    public DateTime LastOpened { get; set; } = DateTime.Now;
    public int FileCount { get; set; }

    /// <summary>显示名：取目录名，根盘符等取全路径兜底。</summary>
    public string Name
    {
        get
        {
            try
            {
                string n = Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrEmpty(n) ? Folder : n;
            }
            catch { return Folder; }
        }
    }

    /// <summary>打开弹窗时由界面层填入的人性化时间（刚刚 / x 分钟前 / 昨天 / yyyy-MM-dd）。</summary>
    public string DisplayTime { get; set; } = string.Empty;

    public void RefreshDisplayTime() => DisplayTime = TimeAgo(LastOpened);

    public static string TimeAgo(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 1.5) return "昨天";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return t.ToString("yyyy-MM-dd");
    }
}

/// <summary>
/// 轻量持久化：记住上次目录 / 打开历史 / 主题 / 字号 / 窗口状态。存放于 %AppData%/MD阅读器/config.json。
/// 行业实践（VS Code / Typora）：静默记住用户偏好，下次启动无缝恢复。
/// </summary>
public sealed class AppConfig
{
    public string? LastFolder { get; set; }
    public string Theme { get; set; } = "light"; // light | dark | system
    public double FontSize { get; set; } = 15;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double SidebarWidth { get; set; } = 300;
    /// <summary>左侧目录栏是否处于收起状态（Ctrl+B 切换，会记住）。</summary>
    public bool SidebarCollapsed { get; set; }
    /// <summary>右侧大纲面板是否展开。默认展开（开箱即见），用户切换后会记住。</summary>
    public bool OutlineExpanded { get; set; } = true;
    /// <summary>打开历史（新）。按最后打开时间倒序，最多保留 12 条。</summary>
    public List<HistoryEntry> History { get; set; } = new();
    /// <summary>旧版字段，仅用于一次性迁移；新记录只写入 <see cref="History"/>。</summary>
    public List<string> RecentFolders { get; set; } = new();

    private static string ConfigPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MD阅读器");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg is not null)
                {
                    if (cfg.FontSize is < 11 or > 24) cfg.FontSize = 15;
                    // 旧版 RecentFolders → History 一次性迁移
                    if ((cfg.History is null || cfg.History.Count == 0) && cfg.RecentFolders is { Count: > 0 })
                    {
                        cfg.History = cfg.RecentFolders
                            .Where(Directory.Exists)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(12)
                            .Select(f => new HistoryEntry { Folder = f, LastOpened = DateTime.Now })
                            .ToList();
                    }
                    cfg.History ??= new();
                    cfg.RecentFolders ??= new();
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏则回退默认，不打扰用户 */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 静默失败 */ }
    }

    /// <summary>记录一次打开：去重、置顶、更新时间与文档数，最多保留 12 条。</summary>
    public void PushHistory(string folder, int fileCount = 0)
    {
        History.RemoveAll(h => string.Equals(h.Folder, folder, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, new HistoryEntry { Folder = folder, LastOpened = DateTime.Now, FileCount = fileCount });
        if (History.Count > 12)
            History = History.Take(12).ToList();
    }

    public void RemoveHistory(string folder)
    {
        History.RemoveAll(h => string.Equals(h.Folder, folder, StringComparison.OrdinalIgnoreCase));
    }

    public void PushRecent(string folder) => PushHistory(folder);
}
