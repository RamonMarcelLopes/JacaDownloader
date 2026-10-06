namespace JacaDownloader;

// One download, both while running and as a history record.
public class Entry
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Platform { get; set; } = "other";
    public string Type { get; set; } = "video";
    public int Quality { get; set; }
    public int? Height { get; set; }
    public string Title { get; set; } = "";
    public string? Uploader { get; set; }
    public double? Duration { get; set; }
    public string? Thumb { get; set; }
    public string OutputDir { get; set; } = "";
    public string? Cookies { get; set; }
    public string? FilePath { get; set; }
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "downloading";
    public string? Error { get; set; }
    public double Percent { get; set; }
    public string Message { get; set; } = "Iniciando...";
}

public class Settings
{
    public string DefaultDir { get; set; } = Downloader.DefaultDir;
    public string ImageDir { get; set; } = Downloader.DefaultDir;
    public string DefaultType { get; set; } = "video";
    public int DefaultQuality { get; set; }
    public string Cookies { get; set; } = "";
    public string Theme { get; set; } = "system";
    public int HistoryDays { get; set; }
    public string LastTab { get; set; } = "download";
}
