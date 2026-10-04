using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JacaDownloader;

// Wraps yt-dlp: metadata lookup and background download jobs.
public static class Downloader
{
    // Where files go when no folder was chosen: the Music folder of whoever runs the app (This PC > Music).
    public static readonly string DefaultDir = ResolveDefaultDir();

    static string ResolveDefaultDir()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return !string.IsNullOrWhiteSpace(music)
            ? music
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
    }

    static readonly HashSet<string> AllowedBrowsers = new() { "chrome", "edge", "firefox", "brave" };

    public static bool IsSpotify(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("spotify.com");

    public static string PlatformOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "other";
        var host = u.Host.ToLowerInvariant();
        bool Is(string d) => host == d || host.EndsWith("." + d);
        if (Is("youtube.com") || Is("youtu.be")) return "youtube";
        if (Is("tiktok.com")) return "tiktok";
        if (Is("instagram.com")) return "instagram";
        if (Is("x.com") || Is("twitter.com") || Is("t.co")) return "x";
        if (Is("spotify.com")) return "spotify";
        return "other";
    }

    public static void ValidateUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ArgumentException("Cole um link válido (http ou https).");
    }

    public static async Task<object> InfoAsync(string url, string? cookies)
    {
        ValidateUrl(url);
        var spotify = IsSpotify(url);
        var target = spotify ? "ytsearch1:" + await SpotifyQueryAsync(url) : url;

        var args = new List<string> { "-J", "--no-playlist", "--no-warnings" };
        AddCookies(args, cookies);
        args.Add(target);

        var (code, stdout, stderr) = await RunAsync(args);
        if (code != 0) throw new Exception(LastError(stderr));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0)
            root = entries[0];

        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = Str(f, "vcodec");
                if (vcodec == "none" || !f.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number) continue;
                heights.Add(h.GetInt32());
            }
        }

        return new
        {
            title = Str(root, "title"),
            uploader = Str(root, "uploader") ?? Str(root, "channel"),
            thumbnail = Str(root, "thumbnail"),
            duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : (double?)null,
            heights,
            spotify,
            platform = PlatformOf(url)
        };
    }

    public static Entry Start(string url, string kind, int height, string? outDir, string? cookies)
    {
        ValidateUrl(url);
        if (kind != "video" && kind != "audio") throw new ArgumentException("Tipo inválido.");

        var settings = Store.Current;
        outDir = string.IsNullOrWhiteSpace(outDir) ? settings.DefaultDir : Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        var entry = new Entry
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = url,
            Platform = PlatformOf(url),
            Type = IsSpotify(url) ? "audio" : kind,
            Quality = Math.Max(height, 0),
            Title = url,
            OutputDir = outDir,
            Cookies = AllowedBrowsers.Contains(cookies ?? "") ? cookies : null
        };
        Store.Add(entry);

        _ = Task.Run(() => RunJobAsync(entry));
        return entry;
    }

    // Starts a fresh download with the same options and drops the failed record.
    public static Entry Retry(Entry old)
    {
        var entry = Start(old.Url, old.Type, old.Quality, old.OutputDir, old.Cookies);
        Store.Remove(old.Id, false);
        return entry;
    }

    static async Task RunJobAsync(Entry job, int attempt = 0)
    {
        string? lastError = null;
        try
        {
            var url = job.Url;
            if (IsSpotify(url))
            {
                job.Message = "Buscando a música no YouTube...";
                url = "ytsearch1:" + await SpotifyQueryAsync(url);
            }

            // Videos carry their resolution in the name so another quality never reuses an existing file.
            var fileName = job.Type == "video" ? "%(title).150B [%(height)sp].%(ext)s" : "%(title).150B.%(ext)s";
            var args = new List<string>
            {
                "--no-playlist", "--newline", "--no-colors", "--no-quiet", "--progress", "--windows-filenames",
                "--encoding", "utf-8",
                "--ffmpeg-location", Tools.Dir,
                "-o", Path.Combine(job.OutputDir, fileName),
                "--print", "before_dl:%(.{title,uploader,duration,thumbnail,height})j",
                "--print", "after_move:filepath"
            };

            if (job.Type == "audio")
            {
                args.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-metadata" });
            }
            else
            {
                var h = job.Quality > 0 ? $"[height<={job.Quality}]" : "";
                var avc = job.Quality > 0 ? $"bv*{h}[vcodec^=avc1]+ba[ext=m4a]/" : "";
                args.AddRange(new[] { "-f", $"{avc}bv*{h}+ba/b{h}", "--merge-output-format", "mp4" });
            }

            AddCookies(args, job.Cookies);
            args.Add(url);

            var startedAt = DateTime.UtcNow.AddSeconds(-5);
            var expectedStreams = job.Type == "video" ? 2 : 1;
            var streamIndex = 0;
            var progress = new Regex(@"\[download\]\s+(\d+(?:\.\d+)?)%");

            using var p = Process.Start(Psi(args))!;
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && e.Data.StartsWith("ERROR")) lastError = e.Data;
            };
            p.BeginErrorReadLine();

            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.StartsWith("{"))
                {
                    ApplyMetadata(job, line);
                }
                else if (line.StartsWith("[download] Destination:"))
                {
                    streamIndex++;
                    job.Message = "Baixando...";
                }
                else if (progress.Match(line) is { Success: true } m)
                {
                    var pct = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var overall = ((Math.Max(streamIndex, 1) - 1) + pct / 100) / expectedStreams * 100;
                    job.Percent = Math.Min(99, overall);
                }
                else if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[VideoConvertor]"))
                {
                    job.Message = job.Type == "audio" ? "Convertendo para MP3..." : "Juntando vídeo e áudio...";
                }
                else if (!line.StartsWith("[") && File.Exists(line))
                {
                    job.FilePath = line;
                }
            }

            await p.WaitForExitAsync();
            if (p.ExitCode == 0 && job.FilePath == null)
                job.FilePath = FindNewestFile(job.OutputDir, job.Type == "audio" ? ".mp3" : ".mp4", startedAt);
            if (p.ExitCode != 0 || job.FilePath == null)
                throw new Exception(lastError != null ? LastError(lastError) : "O download falhou.");

            job.FileSize = new FileInfo(job.FilePath).Length;
            job.Percent = 100;
            job.Message = "Concluído";
            job.Status = "done";
        }
        catch (Exception ex)
        {
            // YouTube sometimes answers 403 on a single attempt; one automatic retry usually succeeds.
            if (attempt == 0 && ex.Message.Contains("403"))
            {
                job.FilePath = null;
                job.Percent = 0;
                job.Message = "Tentando de novo...";
                await RunJobAsync(job, 1);
                return;
            }

            job.Error = ex.Message;
            job.Status = "failed";
        }
        Store.Save();
    }

    // Fallback for when the printed path cannot be matched to a file: take the newest file the job just wrote.
    static string? FindNewestFile(string dir, string extension, DateTime since) =>
        new DirectoryInfo(dir).EnumerateFiles("*" + extension)
            .Where(f => f.LastWriteTimeUtc >= since)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;

    static void ApplyMetadata(Entry job, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            job.Title = Str(r, "title") ?? job.Title;
            job.Uploader = Str(r, "uploader");
            if (r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number) job.Duration = d.GetDouble();
            if (r.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) job.Height = h.GetInt32();

            var thumb = Str(r, "thumbnail");
            if (thumb != null && Uri.TryCreate(thumb, UriKind.Absolute, out var thumbUri) && thumbUri.Scheme.StartsWith("http"))
                _ = SaveThumbAsync(job, thumbUri);
        }
        catch { }
    }

    // Keeps a local copy of the thumbnail so history still shows it after the remote link expires.
    static async Task SaveThumbAsync(Entry job, Uri url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var bytes = await http.GetByteArrayAsync(url);

            var ext = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = ".jpg";
            var name = job.Id + ext;
            await File.WriteAllBytesAsync(Path.Combine(Store.ThumbsDir, name), bytes);

            job.Thumb = name;
            Store.Save();
        }
        catch { }
    }

    // Builds the YouTube search text for a Spotify track page ("artist track").
    static async Task<string> SpotifyQueryAsync(string url)
    {
        if (!Regex.IsMatch(url, @"/track/")) throw new Exception("Por enquanto só links de música (track) do Spotify funcionam.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        var html = await http.GetStringAsync(url);

        var m = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.Singleline);
        var title = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        var parts = Regex.Match(title, @"^(.+?) - (?:song|single)\b.*?\bby (.+?) \| Spotify$");
        if (parts.Success) return $"{parts.Groups[2].Value} {parts.Groups[1].Value}";

        title = title.Replace("| Spotify", "").Trim();
        if (title.Length == 0) throw new Exception("Não consegui ler os dados da música no Spotify.");
        return title;
    }

    static void AddCookies(List<string> args, string? browser)
    {
        if (!string.IsNullOrEmpty(browser) && AllowedBrowsers.Contains(browser))
        {
            args.Add("--cookies-from-browser");
            args.Add(browser);
        }
    }

    static ProcessStartInfo Psi(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Tools.YtDlp)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static async Task<(int Code, string Stdout, string Stderr)> RunAsync(IEnumerable<string> args)
    {
        using var p = Process.Start(Psi(args))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }

    static string LastError(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.StartsWith("ERROR")) ?? text.Trim();
        return Friendly(Regex.Replace(line, @"^ERROR:\s*(\[[^\]]+\]\s*[\w-]*:\s*)?", "").Trim());
    }

    // Translates the most common yt-dlp errors into plain Portuguese.
    static string Friendly(string message)
    {
        var rules = new (string Pattern, string Text)[]
        {
            (@"private video", "Este vídeo é privado."),
            (@"unavailable|has been removed|no longer available|does not exist", "Este conteúdo não está disponível ou foi removido."),
            (@"confirm your age|age-restricted|age restricted", "Este conteúdo exige confirmação de idade. Escolha seu navegador em Opções avançadas para usar seu login."),
            (@"login required|sign in|log in|cookies|rate-limit|empty media response|restricted video", "Este conteúdo exige login. Escolha seu navegador em Opções avançadas para usar seu login."),
            (@"unsupported url", "Esse link não é suportado."),
            (@"HTTP Error 403", "O site recusou o download (erro 403). Tente de novo em instantes."),
            (@"HTTP Error 429|too many requests", "O site bloqueou temporariamente por excesso de pedidos. Tente de novo em alguns minutos."),
            (@"getaddrinfo|unable to download webpage|timed out|connection (reset|refused)|network is unreachable", "Não foi possível conectar. Verifique sua internet."),
            (@"no space left|not enough space", "Sem espaço livre no disco."),
            (@"permission denied|access is denied", "Sem permissão para salvar nessa pasta."),
        };

        foreach (var (pattern, text) in rules)
            if (Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase)) return text;
        return message;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
