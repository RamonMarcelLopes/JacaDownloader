using System.Diagnostics;
using System.Reflection;
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
        // The web view profile always lives in the app data folder, never next to the exe.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(Tools.AppDataDir, "webview"));

        ApplicationConfiguration.Initialize();
        Store.Load();

        var web = BuildWebApp();
        web.Start();
        _ = Tools.EnsureAsync();

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
