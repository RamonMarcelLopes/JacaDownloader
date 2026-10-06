using System.Text.Json;

namespace JacaDownloader;

// Persists settings and download history as JSON files in the app data folder.
public static class Store
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly string SettingsPath = Path.Combine(Tools.AppDataDir, "settings.json");
    static readonly string HistoryPath = Path.Combine(Tools.AppDataDir, "history.json");

    public static readonly string ThumbsDir = Path.Combine(Tools.AppDataDir, "thumbs");

    static readonly string[] Themes = { "system", "light", "dark" };
    static readonly string[] Tabs = { "download", "history", "converter", "settings" };
    static readonly string[] Browsers = { "", "chrome", "edge", "firefox", "brave" };
    static readonly int[] RetentionDays = { 0, 30, 90, -1 };

    public static Settings Current { get; private set; } = new();
    static List<Entry> history = new();

    public static void Load()
    {
        Directory.CreateDirectory(ThumbsDir);
        lock (Gate)
        {
            Current = Read<Settings>(SettingsPath) ?? new Settings();
            history = Read<List<Entry>>(HistoryPath) ?? new List<Entry>();

            foreach (var e in history.Where(e => e.Status == "downloading"))
            {
                e.Status = "failed";
                e.Error = "O download foi interrompido.";
            }
            PurgeExpired();
            SaveHistory();
        }
    }

    public static Settings Update(Settings s)
    {
        lock (Gate)
        {
            var dir = string.IsNullOrWhiteSpace(s.DefaultDir) ? Downloader.DefaultDir : Path.GetFullPath(s.DefaultDir);
            var next = new Settings
            {
                DefaultDir = dir,
                ImageDir = string.IsNullOrWhiteSpace(s.ImageDir) ? dir : Path.GetFullPath(s.ImageDir),
                DefaultType = s.DefaultType == "audio" ? "audio" : "video",
                DefaultQuality = Math.Clamp(s.DefaultQuality, 0, 8640),
                Cookies = Browsers.Contains(s.Cookies ?? "") ? s.Cookies ?? "" : "",
                Theme = Themes.Contains(s.Theme) ? s.Theme : "system",
                HistoryDays = RetentionDays.Contains(s.HistoryDays) ? s.HistoryDays : 0,
                LastTab = Tabs.Contains(s.LastTab) ? s.LastTab : "download"
            };

            if (next.HistoryDays == -1 && Current.HistoryDays != -1)
            {
                foreach (var e in history.Where(e => e.Status != "downloading")) DeleteThumb(e);
                history.RemoveAll(e => e.Status != "downloading");
            }

            Current = next;
            PurgeExpired();
            Write(SettingsPath, Current);
            SaveHistory();
            return Current;
        }
    }

    public static void Add(Entry e)
    {
        lock (Gate)
        {
            history.Add(e);
            SaveHistory();
        }
    }

    public static Entry? Find(string id)
    {
        lock (Gate) return history.FirstOrDefault(e => e.Id == id);
    }

    public static int ActiveCount()
    {
        lock (Gate) return history.Count(e => e.Status == "downloading");
    }

    public static List<Entry> List()
    {
        lock (Gate)
        {
            PurgeExpired();
            var items = Current.HistoryDays == -1 ? history.Where(e => e.Status == "downloading") : history;
            return items.OrderByDescending(e => e.CreatedAt).ToList();
        }
    }

    public static void Save()
    {
        lock (Gate) SaveHistory();
    }

    public static bool Remove(string id, bool deleteFile)
    {
        lock (Gate)
        {
            var e = history.FirstOrDefault(x => x.Id == id);
            if (e == null || e.Status == "downloading") return false;

            if (deleteFile && e.FilePath != null && File.Exists(e.FilePath)) File.Delete(e.FilePath);
            DeleteThumb(e);
            history.Remove(e);
            SaveHistory();
            return true;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            foreach (var e in history.Where(e => e.Status != "downloading")) DeleteThumb(e);
            history.RemoveAll(e => e.Status != "downloading");
            SaveHistory();
        }
    }

    static void PurgeExpired()
    {
        if (Current.HistoryDays <= 0) return;
        var limit = DateTime.UtcNow.AddDays(-Current.HistoryDays);
        foreach (var e in history.Where(e => e.Status != "downloading" && e.CreatedAt < limit)) DeleteThumb(e);
        history.RemoveAll(e => e.Status != "downloading" && e.CreatedAt < limit);
    }

    static void DeleteThumb(Entry e)
    {
        if (e.Thumb == null) return;
        var path = Path.Combine(ThumbsDir, Path.GetFileName(e.Thumb));
        if (File.Exists(path)) File.Delete(path);
    }

    static void SaveHistory()
    {
        if (Current.HistoryDays == -1) return;
        Write(HistoryPath, history);
    }

    static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch { return default; }
    }

    static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, true);
    }
}
