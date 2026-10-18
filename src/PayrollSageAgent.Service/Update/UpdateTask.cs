using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using PayrollSageAgent.Core;
using PayrollSageAgent.Core.Update;

namespace PayrollSageAgent.Service.Update;

/// <summary>
/// The scheduled task that runs the updater — « \IGF\PayrollSageAgent-MiseAJour », every hour, as SYSTEM — and
/// the RUNNER it starts: a copy of the exe in <c>update\runner-X.Y.Z\</c>. [HARD] The copy, because an install
/// replaces the program's own exe, and Windows cannot replace a running one without a reboot; the copy is in
/// no product's file list, so Windows Installer never touches it.
///
/// <c>configure-service</c> (the MSI, as SYSTEM, at every install and upgrade) calls <see cref="InstallAsync"/>:
/// the copy of THIS version, <c>runner.txt</c> pointing at it, and the task re-registered on it — while the
/// runner of the version before may still be running the install, from its own folder. <c>purge</c> (a real
/// uninstall) calls <see cref="RemoveAsync"/>. Written and compiled here, never run here: Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpdateTask
{
    public const string Name = @"\IGF\PayrollSageAgent-MiseAJour";

    public static async Task InstallAsync(AgentHome home, Func<string, Task> say)
    {
        var paths = home.Update;
        var version = AgentProtocol.BuildVersion;
        string runner;
        try
        {
            Directory.CreateDirectory(paths.Folder);
            var folder = paths.RunnerFolder(version);
            Directory.CreateDirectory(folder);
            runner = Path.Combine(folder, "PayrollSageAgent.exe");
            foreach (var file in new[] { "PayrollSageAgent.exe", "appsettings.json" })
            {
                var source = Path.Combine(AgentHome.ProgramFolder, file);
                var target = Path.Combine(folder, file);
                if (File.Exists(source) && !(File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)) File.Copy(source, target, overwrite: true);
            }
            await File.WriteAllTextAsync(paths.RunnerPointer, runner);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await say($"Mises à jour : la copie de l’agent pour la tâche planifiée a échoué ({error.GetType().Name} : {error.Message}).");
            return;
        }

        // Each server at its own minute of the hour, so a hosting company's servers do not all ask at once.
        var minute = 5 + (int)((uint)StableHash(Environment.MachineName) % 50);
        var (exit, said) = await SchtasksAsync("/Create", "/F", "/TN", Name, "/SC", "HOURLY", "/MO", "1",
            "/ST", $"00:{minute.ToString("00", CultureInfo.InvariantCulture)}", "/RU", "SYSTEM", "/RL", "HIGHEST",
            "/TR", $"\"{runner}\" update --scheduled");
        await say(exit == 0
            ? $"Mises à jour : tâche planifiée « {Name} » — toutes les heures, à la minute {minute}."
            : $"Mises à jour : la tâche planifiée n’a pas pu être créée (schtasks, code {exit}) : {said}");
    }

    public static async Task RemoveAsync(AgentHome home, Func<string, Task> say)
    {
        var (exit, _) = await SchtasksAsync("/Delete", "/F", "/TN", Name);
        if (exit == 0) await say($"Tâche planifiée « {Name} » supprimée.");
        try
        {
            if (Directory.Exists(home.Update.Folder)) Directory.Delete(home.Update.Folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await say($"Le dossier {home.Update.Folder} n’a pas pu être effacé ({error.GetType().Name}).");
        }
    }

    /// <summary>The runner folders of versions no longer installed — never this one, never the one runner.txt names.</summary>
    public static void RemoveOldRunners(AgentHome home)
    {
        var paths = home.Update;
        if (!Directory.Exists(paths.Folder)) return;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) };
        try
        {
            if (File.Exists(paths.RunnerPointer) && Path.GetDirectoryName(File.ReadAllText(paths.RunnerPointer).Trim()) is { } current) keep.Add(current);
        }
        catch (IOException)
        {
            return;
        }
        foreach (var folder in Directory.EnumerateDirectories(paths.Folder, UpdatePaths.RunnerPrefix + "*"))
        {
            if (keep.Contains(Path.TrimEndingDirectorySeparator(folder))) continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Still running, or locked: the next run tries again.
            }
        }
    }

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var c in text.ToUpperInvariant()) hash = unchecked((hash * 31) + c);
        return hash;
    }

    private static async Task<(int Exit, string Said)> SchtasksAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var said = (await process.StandardOutput.ReadToEndAsync()) + (await process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        return (process.ExitCode, string.Join(' ', said.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim());
    }
}
