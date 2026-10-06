using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using JacaDownloader;

internal static class Program
{
    // Set by the main window so the API can show the native folder picker.
    public static Func<string?, string?>? PickFolder;

    static readonly string[] OpenableExtensions =
        { ".mp4", ".mp3", ".mkv", ".webm", ".m4a", ".mov", ".opus", ".ogg", ".wav", ".flac" };

    [STAThread]
    static void Main()
    {
        // Handles the installer/updater hooks and must run before anything else.
        Velopack.VelopackApp.Build().Run();

        // The web view profile always lives in the app data folder, never next to the exe.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(Tools.AppDataDir, "webview"));

        ApplicationConfiguration.Initialize();
        Store.Load();
        Converter.CleanTemp();

        var web = BuildWebApp();
        web.Start();
        _ = Tools.EnsureAsync();
        _ = Updater.RunAsync();

        Application.Run(new MainForm(web.Urls.First()));
        web.StopAsync().GetAwaiter().GetResult();
    }

    static object Dto(Entry e) => new
    {
        e.Id, e.Url, e.Platform, e.Type, e.Quality, e.Height, e.Title, e.Uploader, e.Duration,
        thumb = e.Thumb != null ? "/thumbs/" + e.Thumb : null,
        e.OutputDir, e.Cookies, e.FilePath, e.FileSize, e.CreatedAt, e.Status, e.Error, e.Percent, e.Message,
        fileExists = e.FilePath != null && File.Exists(e.FilePath)
    };

    static WebApplication BuildWebApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        // Only the local window may call the API (blocks other sites and DNS rebinding).
        var localHosts = new[] { "localhost", "127.0.0.1" };
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var badOrigin = origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !localHosts.Contains(o.Host));
            if (!localHosts.Contains(ctx.Request.Host.Host) || badOrigin)
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            await next();
        });

        var notReady = Results.Json(new { error = "As ferramentas ainda estão sendo preparadas." }, statusCode: 503);

        app.MapGet("/api/status", () => new
        {
            ready = Tools.Ready,
            message = Tools.Message,
            error = Tools.Error,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        });

        app.MapGet("/api/update", () => new { version = Updater.AvailableVersion, installing = Updater.Installing });

        app.MapPost("/api/update/install", async () =>
        {
            if (Store.ActiveCount() > 0)
                return Results.BadRequest(new { error = "Espere os downloads terminarem para atualizar." });
            try
            {
                await Updater.InstallAsync();
                return Results.Ok();
            }
            catch (Exception ex) { return Results.BadRequest(new { error = "Não foi possível atualizar: " + ex.Message }); }
        });

        app.MapGet("/api/settings", () => Store.Current);
        app.MapPost("/api/settings", (Settings s) =>
        {
            try { return Results.Ok(Store.Update(s)); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/info", async (InfoRequest r) =>
        {
            if (!Tools.Ready) return notReady;
            try { return Results.Ok(await Downloader.InfoAsync(r.Url, r.Cookies)); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/download", (DownloadRequest r) =>
        {
            if (!Tools.Ready) return notReady;
            try { return Results.Ok(Dto(Downloader.Start(r.Url, r.Kind, r.Height ?? 0, r.OutputDir, r.Cookies))); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/history", () => new
        {
            items = Store.List().Select(Dto),
            active = Store.ActiveCount()
        });

        app.MapPost("/api/history/clear", () =>
        {
            Store.Clear();
            return Results.Ok();
        });

        app.MapPost("/api/history/{id}/show", (string id) =>
        {
            var e = Store.Find(id);
            if (e?.FilePath == null || !File.Exists(e.FilePath)) return Results.NotFound(new { error = "Arquivo não encontrado." });
            Process.Start("explorer.exe", $"/select,\"{e.FilePath}\"");
            return Results.Ok();
        });

        app.MapPost("/api/history/{id}/open", (string id) =>
        {
            var e = Store.Find(id);
            if (e?.FilePath == null || !File.Exists(e.FilePath)) return Results.NotFound(new { error = "Arquivo não encontrado." });
            if (!OpenableExtensions.Contains(Path.GetExtension(e.FilePath).ToLowerInvariant()))
                return Results.BadRequest(new { error = "Tipo de arquivo não suportado." });
            Process.Start(new ProcessStartInfo(e.FilePath) { UseShellExecute = true });
            return Results.Ok();
        });

        app.MapPost("/api/history/{id}/retry", (string id) =>
        {
            if (!Tools.Ready) return notReady;
            var e = Store.Find(id);
            if (e == null || e.Status != "failed") return Results.NotFound();
            try { return Results.Ok(Dto(Downloader.Retry(e))); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/history/{id}/remove", (string id, RemoveRequest r) =>
        {
            try { return Store.Remove(id, r.DeleteFile) ? Results.Ok() : Results.NotFound(); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/thumbs/{name}", (string name) =>
        {
            var path = Path.Combine(Store.ThumbsDir, Path.GetFileName(name));
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });

        app.MapPost("/api/pick-folder", (PickFolderRequest r) =>
            Results.Ok(new { path = PickFolder?.Invoke(r.Initial) }));

        app.MapGet("/api/convert/formats", () => new { sources = Converter.Sources, destinations = Converter.Destinations });

        // The image is uploaded as the raw request body, converted locally and kept in a temp folder until it is saved.
        app.MapPost("/api/convert", async (HttpContext ctx, string name, string to) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = null;
            var from = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            if (!Converter.Sources.Contains(from)) return Results.BadRequest(new { error = "Este formato de imagem não é suportado." });
            if (!Converter.Destinations.Contains(to.ToLowerInvariant())) return Results.BadRequest(new { error = "Não dá para converter para este formato." });

            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Converter.TempDir);
            var source = Path.Combine(Converter.TempDir, id + "-source." + from);
            try
            {
                await using (var file = File.Create(source)) await ctx.Request.Body.CopyToAsync(file);
                var output = await Converter.ConvertAsync(source, from, to, id);
                return Results.Ok(new { id, size = new FileInfo(output).Length });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "Não foi possível converter este arquivo." });
            }
            finally { try { File.Delete(source); } catch { } }
        });

        app.MapPost("/api/convert/save", (ConvertSaveRequest r) =>
        {
            var temp = Converter.FindTemp(r.Id);
            if (temp == null) return Results.NotFound(new { error = "A conversão não existe mais. Converta de novo." });
            try
            {
                var dir = string.IsNullOrWhiteSpace(r.Dir) ? Store.Current.ImageDir : Path.GetFullPath(r.Dir);
                Directory.CreateDirectory(dir);
                var invalid = Path.GetInvalidFileNameChars();
                var baseName = new string(Path.GetFileNameWithoutExtension(r.Name).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
                if (baseName.Length == 0) baseName = "imagem";
                var ext = Path.GetExtension(temp);
                var target = Path.Combine(dir, baseName + ext);
                for (var i = 1; File.Exists(target); i++) target = Path.Combine(dir, $"{baseName} ({i}){ext}");
                File.Copy(temp, target);
                return Results.Ok(new { path = target });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = "Não foi possível salvar: " + ex.Message }); }
        });

        app.MapPost("/api/convert/discard", (ConvertDiscardRequest r) =>
        {
            var temp = Converter.FindTemp(r.Id);
            if (temp != null) try { File.Delete(temp); } catch { }
            return Results.Ok();
        });

        app.MapGet("/api/tools", async () =>
            Tools.Ready ? Results.Ok(await Tools.VersionsAsync()) : notReady);

        app.MapPost("/api/tools/update", async () =>
        {
            if (!Tools.Ready) return notReady;
            if (Store.ActiveCount() > 0)
                return Results.BadRequest(new { error = "Espere os downloads terminarem para atualizar." });
            var output = await Tools.UpdateYtDlpAsync();
            return Results.Ok(new { output, versions = await Tools.VersionsAsync() });
        });

        app.MapPost("/api/tools/retry", () =>
        {
            _ = Tools.EnsureAsync();
            return Results.Ok();
        });

        MapEmbeddedUi(app);
        return app;
    }

    // Serves the exported Next.js interface that is embedded in the executable.
    static void MapEmbeddedUi(WebApplication app)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("ui/"))
            .ToDictionary(n => n["ui/".Length..].Replace('\\', '/'), n => n);
        var types = new FileExtensionContentTypeProvider();

        app.MapFallback("{*path}", async ctx =>
        {
            var path = ctx.Request.Path.Value!.TrimStart('/');
            if (path.Length == 0) path = "index.html";
            if (!files.TryGetValue(path, out var resource) && !files.TryGetValue(path + ".html", out resource))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            types.TryGetContentType(path, out var type);
            ctx.Response.ContentType = type ?? "application/octet-stream";
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            await stream.CopyToAsync(ctx.Response.Body);
        });
    }
}

record InfoRequest(string Url, string? Cookies);
record DownloadRequest(string Url, string Kind, int? Height, string? OutputDir, string? Cookies);
record RemoveRequest(bool DeleteFile);
record PickFolderRequest(string? Initial);
record ConvertSaveRequest(string Id, string Name, string? Dir);
record ConvertDiscardRequest(string Id);
