using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NotesSetup;

/// Per-user installer, no admin rights needed.
/// Installs to %LOCALAPPDATA%\Programs\Notes, adds a Start Menu shortcut (that is what Windows Search finds),
/// optionally starts with Windows, and registers an uninstaller under Settings > Apps.
internal static class Setup
{
    private const string AppName = "Notes";
    private const string Version = "1.0.0";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Notes.QuickCapture";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
    private static readonly string ExePath = Path.Combine(InstallDir, "Notes.exe");
    private static readonly string Shortcut = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr h, string text, string caption, uint type);
    private const uint MB_OK = 0, MB_YESNOCANCEL = 3, MB_YESNO = 4, MB_ICONERR = 0x10, MB_ICONQ = 0x20, MB_ICONINFO = 0x40;
    private const int IDYES = 6, IDNO = 7;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            bool quiet = args.Contains("/quiet", StringComparer.OrdinalIgnoreCase);
            if (args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase)) return Uninstall(quiet);
            return Install(quiet);
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, "Something went wrong:\n\n" + ex.Message, AppName + " Setup", MB_OK | MB_ICONERR);
            return 1;
        }
    }

    private static int Install(bool quiet)
    {
        bool autostart = false;
        if (!quiet)
        {
            bool upgrade = File.Exists(ExePath);
            var answer = MessageBox(IntPtr.Zero,
                (upgrade ? $"Update {AppName} to version {Version}?" : $"Install {AppName} {Version}?") +
                $"\n\nLocation: {InstallDir}\n\nStart {AppName} automatically when you sign in to Windows?\n\n" +
                "Yes  =  install and start with Windows\nNo  =  install only\nCancel  =  don't install",
                AppName + " Setup", MB_YESNOCANCEL | MB_ICONQ);
            if (answer != IDYES && answer != IDNO) return 0;
            autostart = answer == IDYES;
        }

        StopRunningApp();
        if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true);
        Directory.CreateDirectory(InstallDir);

        using (var payload = typeof(Setup).Assembly.GetManifestResourceStream("payload.zip")
                             ?? throw new InvalidOperationException("Installer payload is missing."))
        using (var zip = new ZipArchive(payload))
            zip.ExtractToDirectory(InstallDir, overwriteFiles: true);

        // This installer doubles as the uninstaller.
        var uninstaller = Path.Combine(InstallDir, "Uninstall.exe");
        File.Copy(Environment.ProcessPath!, uninstaller, overwrite: true);

        CreateShortcut(Shortcut, ExePath);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "HenningDev");
            key.SetValue("DisplayIcon", ExePath);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024), RegistryValueKind.DWord);
        }

        // A quiet install (used for updates) leaves the start-with-Windows choice as it was.
        if (!quiet)
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autostart) run.SetValue(AppName, $"\"{ExePath}\"");
            else run.DeleteValue(AppName, throwOnMissingValue: false);
        }

        if (quiet) return 0;
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
        MessageBox(IntPtr.Zero,
            $"{AppName} is installed and running.\n\nType \"Notes\" in the Start menu search to open it, " +
            "or use your global hotkey (Ctrl+Alt+N by default).",
            AppName + " Setup", MB_OK | MB_ICONINFO);
        return 0;
    }

    private static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox(IntPtr.Zero,
                $"Remove {AppName} from this PC?\n\nYour notes and settings are kept.",
                AppName + " Uninstall", MB_YESNO | MB_ICONQ) != IDYES) return 0;

        StopRunningApp();
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue(AppName, throwOnMissingValue: false);

        // This exe sits inside the install folder, so a short-lived cmd removes the folder after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });

        if (!quiet) MessageBox(IntPtr.Zero, $"{AppName} was removed.", AppName + " Uninstall", MB_OK | MB_ICONINFO);
        return 0;
    }

    private static void StopRunningApp()
    {
        foreach (var p in Process.GetProcessesByName("Notes"))
        {
            try
            {
                // Our app (installed or a dev build) — it allows one instance, so an old copy would block the new one.
                var file = p.MainModule?.FileName;
                if (file == null || FileVersionInfo.GetVersionInfo(file).ProductName != "Notes") continue;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch { /* not ours, or already gone */ }
        }
    }

    private static void CreateShortcut(string lnk, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.IconLocation = target + ",0";
        sc.Description = "Quick-capture notes";
        sc.Save();
        Marshal.FinalReleaseComObject(sc);
        Marshal.FinalReleaseComObject(shell);
    }

    private static long DirSize(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}
