using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notes;

public class Settings
{
    public string Mode { get; set; } = "Light";          // Light | Dark
    public string Accent { get; set; } = "Honey";        // preset name or "Custom"
    public string CustomAccent { get; set; } = "#FF9F0A";
    public string CustomBackground { get; set; } = "";   // empty = theme default
    public string CustomText { get; set; } = "";         // empty = theme default
    public double Opacity { get; set; } = 0.97;
    public string Font { get; set; } = "Inter";
    public double FontSize { get; set; } = 14;
    public string DefaultFormat { get; set; } = "md";
    public string NotesFolder { get; set; } = "";
    public bool Topmost { get; set; } = false;
    public uint HotkeyMods { get; set; } = 0x3;   // Win32 MOD_ALT | MOD_CONTROL
    public uint HotkeyKey { get; set; } = 0x4E;   // VK_N
    public double WinX { get; set; } = double.NaN;
    public double WinY { get; set; } = double.NaN;
    public double WinW { get; set; } = 460;
    public double WinH { get; set; } = 540;
}

public class NoteItem
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Format { get; set; } = "md";
    public bool Locked { get; set; }
    public DateTime Modified { get; set; }
    public string Preview { get; set; } = "";

    public string Subtitle => Locked
        ? $"🔒 Locked · {Modified:MMM d}"
        : $"{Modified:MMM d} · {(Preview.Length == 0 ? "Empty" : Preview)}";
    public string Display => Locked ? "🔒 " + Title : Title;
}

public static class Store
{
    public static readonly string[] Formats = { "md", "txt", "html", "json", "csv", "log" };
    public const string LockedExt = ".locked";
    public const string DefaultCategory = "General";

    /// Optional override (used for screenshots or a portable setup): keeps settings and notes in one folder.
    private static readonly string? DataOverride = Environment.GetEnvironmentVariable("NOTES_DATA_DIR");

    public static string AppDir { get; } = DataOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notes");
    private static string SettingsPath => Path.Combine(AppDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { /* corrupted settings fall back to defaults */ }
        return new();
    }

    public static void SaveSettings(Settings s)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json));
        }
        catch { }
    }

    public static string Root(Settings s)
    {
        var root = string.IsNullOrWhiteSpace(s.NotesFolder)
            ? (DataOverride != null ? Path.Combine(DataOverride, "notes")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notes"))
            : s.NotesFolder;
        Directory.CreateDirectory(Path.Combine(root, DefaultCategory));
        return root;
    }

    public static List<string> Categories(string root) =>
        Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.StartsWith('.'))
            .Select(n => n!)
            .OrderBy(n => n == DefaultCategory ? "" : n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<NoteItem> Notes(string root)
    {
        var list = new List<NoteItem>();
        foreach (var cat in Categories(root))
        foreach (var file in Directory.GetFiles(Path.Combine(root, cat)))
        {
            var item = Parse(file, cat);
            if (item != null) list.Add(item);
        }
        return list.OrderByDescending(n => n.Modified).ToList();
    }

    public static NoteItem? Parse(string file, string category)
    {
        var name = Path.GetFileName(file);
        bool locked = name.EndsWith(LockedExt, StringComparison.OrdinalIgnoreCase);
        if (locked) name = name[..^LockedExt.Length];
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (!Formats.Contains(ext)) return null;

        var item = new NoteItem
        {
            Path = file,
            Category = category,
            Title = Path.GetFileNameWithoutExtension(name),
            Format = ext,
            Locked = locked,
            Modified = File.GetLastWriteTime(file),
        };
        if (!locked) item.Preview = MakePreview(SafeRead(file));
        return item;
    }

    public static string MakePreview(string body)
    {
        var line = body.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    public static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); } catch { return ""; }
    }

    public static string Sanitize(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(title.Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 80) clean = clean[..80];
        return clean.Length == 0 ? "Untitled" : clean;
    }

    /// Builds the path a note should live at, avoiding collisions with other files.
    public static string TargetPath(string root, string category, string title, string format, bool locked, string? currentPath)
    {
        var dir = Path.Combine(root, category);
        Directory.CreateDirectory(dir);
        var baseName = Sanitize(title);
        var suffix = "." + format + (locked ? LockedExt : "");
        var path = Path.Combine(dir, baseName + suffix);
        int i = 2;
        while (File.Exists(path) && !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(dir, $"{baseName} ({i++}){suffix}");
        return path;
    }

    /// Deleted notes go to the Windows Recycle Bin, so they can be restored from there.
    public static void Trash(string file) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}

/// AES-256-GCM with a PBKDF2-SHA256 derived key. File layout: magic | salt | nonce | tag | ciphertext.
public static class Crypto
{
    private static readonly byte[] Magic = "NOTESLK1"u8.ToArray();
    private const int Iterations = 310_000;

    public static byte[] Encrypt(string text, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        return [.. Magic, .. salt, .. nonce, .. tag, .. cipher];
    }

    /// Returns null when the password is wrong or the file is damaged.
    public static string? Decrypt(byte[] data, string password)
    {
        int h = Magic.Length;
        if (data.Length < h + 44 || !data.AsSpan(0, h).SequenceEqual(Magic)) return null;
        var salt = data.AsSpan(h, 16).ToArray();
        var nonce = data.AsSpan(h + 16, 12);
        var tag = data.AsSpan(h + 28, 16);
        var cipher = data.AsSpan(h + 44);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
