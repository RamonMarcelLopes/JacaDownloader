using System.Diagnostics;
using System.IO.Compression;

namespace JacaDownloader;

// Locates and downloads the yt-dlp and ffmpeg binaries the app depends on.
public static class Tools
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    public static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JacaDownloader");

    public static readonly string Dir = Path.Combine(AppDataDir, "tools");
    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");
    public static string Ffmpeg => Path.Combine(Dir, "ffmpeg.exe");
    static string UpdateMarker => Path.Combine(Dir, "last-update.txt");

    public static bool Ready { get; private set; }
    public static string Message { get; private set; } = "Preparando ferramentas...";
    public static string? Error { get; private set; }

    public static async Task EnsureAsync()
    {
        try
        {
            Ready = false;
            Error = null;
            Message = "Preparando ferramentas...";
            Directory.CreateDirectory(Dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

            if (!File.Exists(YtDlp))
            {
                Message = "Baixando yt-dlp...";
                await DownloadFileAsync(http, YtDlpUrl, YtDlp);
            }

            if (!File.Exists(Ffmpeg))
            {
                Message = "Baixando ffmpeg (só na primeira vez, ~130 MB)...";
                await DownloadFfmpegAsync(http);
            }

            if (UpdateDue())
            {
                Message = "Verificando atualizações...";
                await UpdateYtDlpAsync();
            }

            Ready = true;
            Message = "Pronto";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Message = "Falha ao preparar as ferramentas";
        }
    }

    public static async Task<object> VersionsAsync()
    {
        var yt = (await RunAsync(YtDlp, "--version")).Trim();
        var ff = (await RunAsync(Ffmpeg, "-version")).Split('\n').FirstOrDefault()?.Trim() ?? "";
        var ffParts = ff.Split(' ');
        return new { ytdlp = yt, ffmpeg = ffParts.Length > 2 ? ffParts[2] : ff };
    }

    // Updates yt-dlp in place and returns its output.
    public static async Task<string> UpdateYtDlpAsync()
    {
        var output = await RunAsync(YtDlp, "-U");
        await File.WriteAllTextAsync(UpdateMarker, DateTime.UtcNow.ToString("O"));
        return output.Trim();
    }

    static bool UpdateDue() =>
        !File.Exists(UpdateMarker) || DateTime.UtcNow - File.GetLastWriteTimeUtc(UpdateMarker) > TimeSpan.FromDays(1);

    static async Task<string> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return await stdout + await stderr;
    }

    static async Task DownloadFileAsync(HttpClient http, string url, string dest)
    {
        var tmp = dest + ".part";
        await using (var src = await http.GetStreamAsync(url))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        File.Move(tmp, dest, true);
    }

    static async Task DownloadFfmpegAsync(HttpClient http)
    {
        var zipPath = Path.Combine(Dir, "ffmpeg.zip");
        await DownloadFileAsync(http, FfmpegUrl, zipPath);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.First(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            entry.ExtractToFile(Ffmpeg, true);
        }
        File.Delete(zipPath);
    }
}
