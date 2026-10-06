using Velopack;
using Velopack.Sources;

namespace JacaDownloader;

// Checks the GitHub releases for a newer version and applies it in place (only when installed through the Setup).
public static class Updater
{
    const string RepoUrl = "https://github.com/RamonMarcelLopes/JacaDownloader";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    static readonly UpdateManager Manager = new(new GithubSource(RepoUrl, null, false));
    static UpdateInfo? pending;

    public static bool Installed => Manager.IsInstalled;
    public static string? AvailableVersion => pending?.TargetFullRelease.Version.ToString();
    public static bool Installing { get; private set; }

    // Looks for an update at startup and then periodically; failures (offline, rate limit) are ignored.
    public static async Task RunAsync()
    {
        if (!Installed) return;
        while (true)
        {
            try { pending = await Manager.CheckForUpdatesAsync() ?? pending; }
            catch { }
            await Task.Delay(CheckEvery);
        }
    }

    // Downloads the pending update and restarts the app into the new version.
    public static async Task InstallAsync()
    {
        if (pending == null) throw new InvalidOperationException("Nenhuma atualização disponível.");
        if (Installing) return;
        Installing = true;
        try
        {
            await Manager.DownloadUpdatesAsync(pending);
            Manager.ApplyUpdatesAndRestart(pending);
        }
        catch
        {
            Installing = false;
            throw;
        }
    }
}
