using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Notes;

public partial class MainWindow : Window
{
    private const string AllNotes = "All notes";
    private const int WM_HOTKEY = 0x0312, HOTKEY_ID = 0x4E41;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private IntPtr _hwnd;

    private readonly Settings _s;
    private string _root;
    private List<NoteItem> _all = new();
    private List<string> _categories = new();

    // Current note state. _cur == null means an unsaved draft.
    private NoteItem? _cur;
    private string _curCategory = Store.DefaultCategory;
    private string _curFormat = "md";
    private bool _curLocked;
    private string? _pw;          // password of the open protected note (memory only)
    private bool _sealed;         // protected note whose content is not decrypted yet
    private bool _loading;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TaskCompletionSource<string?>? _prompt;
    private bool _promptNeedsConfirm;

    public MainWindow() : this(new Settings()) { }

    public MainWindow(Settings settings)
    {
        _s = settings;
        InitializeComponent();
        _root = Store.Root(_s);
        _curFormat = _s.DefaultFormat;
        LogoHost.Content = Logo.Create(18);
        Topmost = _s.Topmost;
        RestoreBounds();
        WireUp();
        BuildSettings();
        ApplyTheme();
        Reload();
        NewDraft();
    }

    // ---------- Window chrome, shape and hotkey ----------

    private void WireUp()
    {
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };

        foreach (var grip in ((Panel)Content!).Children.OfType<Border>().Where(b => b.Classes.Contains("grip")))
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                BeginResizeDrag(grip.Tag switch
                {
                    "W" => WindowEdge.West, "E" => WindowEdge.East, "N" => WindowEdge.North, "S" => WindowEdge.South,
                    "NW" => WindowEdge.NorthWest, "NE" => WindowEdge.NorthEast,
                    "SW" => WindowEdge.SouthWest, _ => WindowEdge.SouthEast,
                }, e);
            };

        TitleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) ApplyShape(Bounds.Width < 300 ? "Normal" : "Slim right");
            else BeginMoveDrag(e);
        };

        NewBtn.Click += (_, _) => { NewDraft(); FocusEditor(); };
        CloseBtn.Click += (_, _) => HideWindow();
        MinBtn.Click += (_, _) => { Flush(); SaveBounds(); WindowState = WindowState.Minimized; };
        SettingsBtn.Click += (_, _) => ToggleSettings(!SettingsPanel.IsVisible);
        DoneBtn.Click += (_, _) => ToggleSettings(false);
        PinBtn.Click += (_, _) => { _s.Topmost = Topmost = !Topmost; UpdatePin(); Store.SaveSettings(_s); };
        ShapeBtn.Click += (_, _) => ShapeMenu().ShowAt(ShapeBtn);
        UpdatePin();

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        CategoryFilter.SelectionChanged += (_, _) => { if (!_loading) ApplyFilter(); };
        AddCategoryBtn.Click += async (_, _) => await AddCategory();
        NoteList.SelectionChanged += (_, _) => { if (!_loading && NoteList.SelectedItem is NoteItem n && n != _cur) Open(n); };
        NotePicker.SelectionChanged += (_, _) => { if (!_loading && NotePicker.SelectedItem is NoteItem n && n != _cur) Open(n); };

        TitleBox.TextChanged += (_, _) => QueueSave();
        BodyBox.TextChanged += (_, _) => QueueSave();
        CategoryBox.SelectionChanged += (_, _) =>
        {
            if (_loading || CategoryBox.SelectedItem is not string c) return;
            _curCategory = c; SaveNow();
        };
        FormatBox.ItemsSource = Store.Formats;
        FormatBox.SelectionChanged += (_, _) =>
        {
            if (_loading || FormatBox.SelectedItem is not string f) return;
            _curFormat = f; SaveNow();
        };
        LockBtn.Click += async (_, _) => await LockClicked();
        UnlockBtn.Click += (_, _) => TryUnlock();
        UnlockBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TryUnlock(); };
        ExportBtn.Click += async (_, _) => await Export();
        DeleteBtn.Click += (_, _) => DeleteCurrent();

        PromptOk.Click += (_, _) => SubmitPrompt();
        PromptCancel.Click += (_, _) => ClosePrompt(null);
        PromptInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) SubmitPrompt(); };
        PromptConfirm.KeyDown += (_, e) => { if (e.Key == Key.Enter) SubmitPrompt(); };

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => UpdateLayoutForSize();
        Opened += (_, _) => { RegisterHotkey(); UpdateLayoutForSize(); };
        Closing += (_, e) =>
        {
            if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) { Flush(); return; }
            e.Cancel = true;
            HideWindow();
        };
    }

    private void RegisterHotkey()
    {
        var hwnd = _hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        Win32Properties.AddWndProcHookCallback(this, (IntPtr h, uint msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && (int)w == HOTKEY_ID)
            {
                handled = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible && IsActive) HideWindow();
                    else ShowCapture(newNote: true);
                });
            }
            return IntPtr.Zero;
        });
        ApplyHotkey();
    }

    /// (Re)registers the global hotkey from settings. Returns false if another app already owns it.
    private bool ApplyHotkey()
    {
        if (_hwnd == IntPtr.Zero) return true;
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        bool ok = RegisterHotKey(_hwnd, HOTKEY_ID, _s.HotkeyMods | MOD_NOREPEAT, _s.HotkeyKey);
        HotkeyStatus.Text = ok ? "Click the box and press a new shortcut." : "That shortcut is used by another app — pick another.";
        return ok;
    }

    private static string HotkeyText(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(vk switch
        {
            >= 0x30 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            0x20 => "Space",
            _ => $"Key {vk:X2}",
        });
        return string.Join(" + ", parts);
    }

    private static uint ToVirtualKey(Key k) => k switch
    {
        >= Key.A and <= Key.Z => 0x41u + (uint)(k - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30u + (uint)(k - Key.D0),
        >= Key.F1 and <= Key.F24 => 0x70u + (uint)(k - Key.F1),
        Key.Space => 0x20,
        _ => 0,
    };

    private void OnHotkeyRecord(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        uint vk = ToVirtualKey(e.Key);
        uint mods = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mods |= MOD_CONTROL;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mods |= MOD_ALT;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mods |= MOD_SHIFT;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) mods |= MOD_WIN;
        if (vk == 0 || mods == 0) { HotkeyStatus.Text = "Use at least one of Ctrl / Alt / Shift / Win plus a letter, digit or F-key."; return; }

        var (oldMods, oldKey) = (_s.HotkeyMods, _s.HotkeyKey);
        _s.HotkeyMods = mods; _s.HotkeyKey = vk;
        if (!ApplyHotkey())
        {
            _s.HotkeyMods = oldMods; _s.HotkeyKey = oldKey;
            var msg = HotkeyStatus.Text;
            ApplyHotkey();
            HotkeyStatus.Text = msg;
        }
        HotkeyBox.Text = HotkeyText(_s.HotkeyMods, _s.HotkeyKey);
        Store.SaveSettings(_s);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (HotkeyBox.IsFocused) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape)
        {
            if (PromptPanel.IsVisible) ClosePrompt(null);
            else if (SettingsPanel.IsVisible) ToggleSettings(false);
            else HideWindow();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N) { NewDraft(); FocusEditor(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { SaveNow(); e.Handled = true; }
        else if (ctrl && e.Key == Key.L && _curLocked && !_sealed) { LockNow(); e.Handled = true; }
    }

    public void Toggle()
    {
        if (IsVisible) HideWindow(); else ShowCapture(newNote: false);
    }

    /// Shows the window; with newNote it opens a fresh draft ready to type into.
    public void ShowCapture(bool newNote)
    {
        if (newNote && (_cur != null || BodyBox.Text?.Length > 0 || TitleBox.Text?.Length > 0)) NewDraft();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        FocusEditor();
    }

    private void HideWindow()
    {
        Flush();
        SaveBounds();
        Hide();
    }

    private void FocusEditor() => Dispatcher.UIThread.Post(() =>
    {
        if (_sealed) UnlockBox.Focus(); else BodyBox.Focus();
    }, DispatcherPriority.Background);

    private void UpdatePin()
    {
        PinBtn.Classes.Set("on", Topmost);
        PinBtn.Content = Topmost ? "" : "";
    }

    /// Adapts the layout so the window works from a thin vertical strip up to full screen.
    private void UpdateLayoutForSize()
    {
        double w = Bounds.Width, h = Bounds.Height;
        bool narrow = w < 400;
        bool tiny = w < 240;
        MainArea.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(Math.Clamp(w * 0.32, 180, 260));
        Sidebar.IsVisible = !narrow;
        NotePicker.IsVisible = narrow && h > 220;
        AppTitle.IsVisible = !tiny;
        ShapeBtn.IsVisible = w >= 200;
        PinBtn.IsVisible = !tiny;
        MetaRow.IsVisible = h > 190;
        EditorArea.Margin = tiny ? new Thickness(10, 4, 8, 8) : new Thickness(16, 8, 12, 10);
        TitleBox.FontSize = tiny ? 16 : 20;
    }

    private static readonly string[] Shapes = { "Normal", "Compact", "Slim right", "Slim left", "Strip top", "Strip bottom", "Fill screen" };

    private MenuFlyout ShapeMenu()
    {
        var menu = new MenuFlyout();
        foreach (var name in Shapes)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => ApplyShape(name);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void ApplyShape(string kind)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        double sc = screen.Scaling;
        double sw = wa.Width / sc, sh = wa.Height / sc;
        (double W, double H, int X, int Y) r = kind switch
        {
            "Slim right" => (220, sh, wa.Right - (int)(220 * sc), wa.Y),
            "Slim left" => (220, sh, wa.X, wa.Y),
            "Strip top" => (sw, 190, wa.X, wa.Y),
            "Strip bottom" => (sw, 190, wa.X, wa.Bottom - (int)(190 * sc)),
            "Fill screen" => (sw, sh, wa.X, wa.Y),
            "Compact" => (320, 240, 0, 0),
            _ => (460, 540, 0, 0),
        };
        if (kind is "Compact" or "Normal")
            r = (r.W, r.H, wa.X + (int)((wa.Width - r.W * sc) / 2), wa.Y + (int)((wa.Height - r.H * sc) / 2));
        Width = r.W; Height = r.H;
        Position = new PixelPoint(r.X, r.Y);
        SaveBounds();
    }

    private void RestoreBounds()
    {
        Width = Math.Max(MinWidth, _s.WinW);
        Height = Math.Max(MinHeight, _s.WinH);
        if (double.IsNaN(_s.WinX) || double.IsNaN(_s.WinY)) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)_s.WinX, (int)_s.WinY);
        Opened += (_, _) =>
        {
            // If the saved spot is off every screen (monitor unplugged), recenter.
            var p = Position;
            if (!Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(p.X + 40, p.Y + 20)))) ApplyShape("Normal");
        };
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _s.WinX = Position.X; _s.WinY = Position.Y;
        _s.WinW = Width; _s.WinH = Height;
        Store.SaveSettings(_s);
    }

    // ---------- Notes list ----------

    private void Reload()
    {
        _loading = true;
        _categories = Store.Categories(_root);
        _all = Store.Notes(_root);
        var filter = CategoryFilter.SelectedItem as string;
        CategoryFilter.ItemsSource = new[] { AllNotes }.Concat(_categories).ToList();
        CategoryFilter.SelectedItem = filter != null && (filter == AllNotes || _categories.Contains(filter)) ? filter : AllNotes;
        CategoryBox.ItemsSource = _categories;
        CategoryBox.SelectedItem = _curCategory;
        if (_cur != null) _cur = _all.FirstOrDefault(n => n.Path.Equals(_cur.Path, StringComparison.OrdinalIgnoreCase)) ?? _cur;
        _loading = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var cat = CategoryFilter.SelectedItem as string ?? AllNotes;
        var q = SearchBox.Text?.Trim() ?? "";
        var shown = _all.Where(n =>
                (cat == AllNotes || n.Category == cat) &&
                (q.Length == 0 || n.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || n.Preview.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _loading = true;
        NoteList.ItemsSource = shown;
        NotePicker.ItemsSource = shown;
        NoteList.SelectedItem = _cur != null && shown.Contains(_cur) ? _cur : null;
        NotePicker.SelectedItem = NoteList.SelectedItem;
        _loading = false;
    }

    private async Task AddCategory()
    {
        var name = await Prompt("New category", "Categories are folders inside your notes folder.", "Name", password: false, confirm: false);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = Store.Sanitize(name);
        Directory.CreateDirectory(System.IO.Path.Combine(_root, name));
        Reload();
        CategoryFilter.SelectedItem = name;
        if (_cur == null) { _curCategory = name; _loading = true; CategoryBox.SelectedItem = name; _loading = false; }
    }

    // ---------- Editing ----------

    private void NewDraft()
    {
        Flush();
        _cur = null;
        _pw = null;
        _curLocked = false;
        _sealed = false;
        var filter = CategoryFilter.SelectedItem as string;
        _curCategory = filter != null && filter != AllNotes ? filter : Store.DefaultCategory;
        _curFormat = Store.Formats.Contains(_s.DefaultFormat) ? _s.DefaultFormat : "md";
        ShowNote("", "");
        ApplyFilter();
    }

    private void Open(NoteItem n)
    {
        Flush();
        _cur = n;
        _curCategory = n.Category;
        _curFormat = n.Format;
        _curLocked = n.Locked;
        _pw = null;
        _sealed = n.Locked;
        ShowNote(n.Title, n.Locked ? "" : Store.SafeRead(n.Path));
        ApplyFilter();
        if (_sealed) FocusEditor();
    }

    private void ShowNote(string title, string body)
    {
        _loading = true;
        TitleBox.Text = title;
        BodyBox.Text = body;
        CategoryBox.SelectedItem = _curCategory;
        FormatBox.SelectedItem = _curFormat;
        _loading = false;
        UnlockBox.Text = "";
        UnlockError.IsVisible = false;
        UpdateLockUi();
    }

    private void UpdateLockUi()
    {
        LockedPanel.IsVisible = _sealed;
        BodyBox.IsVisible = !_sealed;
        TitleBox.IsReadOnly = _sealed;
        CategoryBox.IsEnabled = FormatBox.IsEnabled = ExportBtn.IsEnabled = !_sealed;
        LockBtn.Classes.Set("on", _curLocked);
        LockBtn.Content = _curLocked ? "" : "";
        ToolTip.SetTip(LockBtn, _curLocked ? "Protected — click for options" : "Protect with a password");
    }

    private void QueueSave()
    {
        if (_loading || _sealed) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        Save();
    }

    public void Flush()
    {
        if (_saveTimer.IsEnabled) SaveNow();
    }

    private void Save()
    {
        if (_sealed) return;
        var title = TitleBox.Text?.Trim() ?? "";
        var body = BodyBox.Text ?? "";
        if (_cur == null && title.Length == 0 && body.Trim().Length == 0) return;
        if (_curLocked && _pw == null) return;

        var name = title.Length > 0 ? title : (Store.MakePreview(body) is { Length: > 0 } p ? p.TrimEnd('…') : "Untitled");
        if (name.Length > 40) name = name[..40];
        try
        {
            var target = Store.TargetPath(_root, _curCategory, name, _curFormat, _curLocked, _cur?.Path);
            if (_cur != null && File.Exists(_cur.Path) && !target.Equals(_cur.Path, StringComparison.OrdinalIgnoreCase))
                File.Move(_cur.Path, target);
            if (_curLocked) File.WriteAllBytes(target, Crypto.Encrypt(body, _pw!));
            else File.WriteAllText(target, body);

            var fresh = Store.Parse(target, _curCategory)!;
            if (_curLocked) fresh.Preview = "";
            if (_cur == null) _all.Insert(0, fresh);
            else
            {
                var i = _all.IndexOf(_cur);
                if (i >= 0) _all[i] = fresh; else _all.Insert(0, fresh);
            }
            _cur = fresh;
            _all = _all.OrderByDescending(n => n.Modified).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppTitle.Text = "Save failed";
            ToolTip.SetTip(AppTitle, ex.Message);
        }
    }

    private void DeleteCurrent()
    {
        if (_cur == null) { NewDraft(); return; }
        _saveTimer.Stop();
        try { if (File.Exists(_cur.Path)) Store.Trash(_cur.Path); } catch { }
        _all.Remove(_cur);
        _cur = null;
        _sealed = false;
        NewDraft();
    }

    private async Task Export()
    {
        if (_sealed) return;
        SaveNow();
        var types = Store.Formats.Select(f => new FilePickerFileType(f.ToUpperInvariant()) { Patterns = new[] { "*." + f } }).ToList();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export note",
            SuggestedFileName = Store.Sanitize(TitleBox.Text ?? "Untitled"),
            DefaultExtension = _curFormat,
            FileTypeChoices = types.OrderBy(t => t.Name != _curFormat.ToUpperInvariant()).ToList(),
            ShowOverwritePrompt = true,
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(BodyBox.Text ?? "");
    }

    // ---------- Password protection ----------

    private async Task LockClicked()
    {
        if (_sealed) { UnlockBox.Focus(); return; }
        if (!_curLocked)
        {
            var pw = await Prompt("Protect this note", "The note is encrypted with AES-256. If you forget the password, the note cannot be recovered.",
                "Password", password: true, confirm: true);
            if (string.IsNullOrEmpty(pw)) return;
            _curLocked = true;
            _pw = pw;
            UpdateLockUi();
            SaveNow();
            return;
        }

        var menu = new MenuFlyout();
        void Add(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        Add("Lock now   Ctrl+L", LockNow);
        Add("Change password…", async () =>
        {
            var pw = await Prompt("Change password", "Enter a new password for this note.", "New password", password: true, confirm: true);
            if (string.IsNullOrEmpty(pw)) return;
            _pw = pw;
            SaveNow();
        });
        Add("Remove password", () => { _curLocked = false; _pw = null; UpdateLockUi(); SaveNow(); });
        menu.ShowAt(LockBtn);
    }

    private void LockNow()
    {
        SaveNow();
        if (_cur == null) return;
        _pw = null;
        _sealed = true;
        ShowNote(_cur.Title, "");
        FocusEditor();
    }

    private void TryUnlock()
    {
        if (_cur == null) return;
        var pw = UnlockBox.Text ?? "";
        string? text = null;
        try { text = Crypto.Decrypt(File.ReadAllBytes(_cur.Path), pw); } catch { }
        if (text == null) { UnlockError.IsVisible = true; UnlockBox.SelectAll(); return; }
        _pw = pw;
        _sealed = false;
        ShowNote(_cur.Title, text);
        FocusEditor();
    }

    // ---------- Prompt overlay ----------

    private Task<string?> Prompt(string title, string hint, string watermark, bool password, bool confirm)
    {
        _prompt?.TrySetResult(null);
        _prompt = new TaskCompletionSource<string?>();
        _promptNeedsConfirm = confirm;
        PromptTitle.Text = title;
        PromptHint.Text = hint;
        PromptInput.Text = PromptConfirm.Text = "";
        PromptInput.Watermark = watermark;
        PromptInput.PasswordChar = password ? '•' : '\0';
        PromptConfirm.IsVisible = confirm;
        PromptError.IsVisible = false;
        PromptPanel.IsVisible = true;
        Dispatcher.UIThread.Post(() => PromptInput.Focus(), DispatcherPriority.Background);
        return _prompt.Task;
    }

    private void SubmitPrompt()
    {
        var value = PromptInput.Text ?? "";
        if (value.Length == 0) { ShowPromptError("Please enter a value."); return; }
        if (_promptNeedsConfirm && value != PromptConfirm.Text) { ShowPromptError("Passwords don't match."); return; }
        ClosePrompt(value);
    }

    private void ShowPromptError(string msg)
    {
        PromptError.Text = msg;
        PromptError.IsVisible = true;
    }

    private void ClosePrompt(string? value)
    {
        PromptPanel.IsVisible = false;
        PromptInput.Text = PromptConfirm.Text = "";
        var tcs = _prompt;
        _prompt = null;
        tcs?.TrySetResult(value);
        FocusEditor();
    }

    // ---------- Settings ----------

    private void ToggleSettings(bool show)
    {
        if (show) { Flush(); FolderBox.Text = _root; }
        SettingsPanel.IsVisible = show;
        if (!show) FocusEditor();
    }

    private void BuildSettings()
    {
        LightBtn.Click += (_, _) => { _s.Mode = "Light"; ApplyTheme(); };
        DarkBtn.Click += (_, _) => { _s.Mode = "Dark"; ApplyTheme(); };

        foreach (var (name, hex) in Themes.Accents)
        {
            var b = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(15), Background = Brushes.Transparent, Tag = name,
                Content = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(Color.Parse(hex)) },
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(b, name);
            b.Click += (_, _) => { _s.Accent = name; ApplyTheme(); };
            Swatches.Children.Add(b);
        }

        AccentHex.Text = _s.Accent == "Custom" ? _s.CustomAccent : "";
        AccentHex.TextChanged += (_, _) =>
        {
            if (!Themes.TryParse(AccentHex.Text, out _)) return;
            _s.Accent = "Custom"; _s.CustomAccent = AccentHex.Text!.Trim(); ApplyTheme();
        };
        BgHex.Text = _s.CustomBackground;
        BgHex.TextChanged += (_, _) => ColorField(BgHex, v => _s.CustomBackground = v);
        TextHex.Text = _s.CustomText;
        TextHex.TextChanged += (_, _) => ColorField(TextHex, v => _s.CustomText = v);

        OpacitySlider.Value = _s.Opacity;
        OpacitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _s.Opacity = Math.Round(OpacitySlider.Value, 2); ApplyTheme();
        };

        FontBox.ItemsSource = Themes.Fonts;
        FontBox.SelectedItem = Themes.Fonts.Contains(_s.Font) ? _s.Font : "Inter";
        FontBox.SelectionChanged += (_, _) => { if (FontBox.SelectedItem is string f) { _s.Font = f; ApplyTheme(); } };
        FontSizeSlider.Value = _s.FontSize;
        FontSizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _s.FontSize = FontSizeSlider.Value; ApplyTheme();
        };

        foreach (var shape in Shapes)
        {
            var b = new Button { Content = shape, Margin = new Thickness(0, 0, 6, 6) };
            b.Classes.Add("pill");
            b.Click += (_, _) => ApplyShape(shape);
            ShapePresets.Children.Add(b);
        }

        HotkeyBox.Text = HotkeyText(_s.HotkeyMods, _s.HotkeyKey);
        HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyRecord, RoutingStrategies.Tunnel);

        DefaultFormatBox.ItemsSource = Store.Formats;
        DefaultFormatBox.SelectedItem = Store.Formats.Contains(_s.DefaultFormat) ? _s.DefaultFormat : "md";
        DefaultFormatBox.SelectionChanged += (_, _) =>
        {
            if (DefaultFormatBox.SelectedItem is string f) { _s.DefaultFormat = f; Store.SaveSettings(_s); }
        };

        BrowseBtn.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose notes folder" });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            Flush();
            _s.NotesFolder = path;
            Store.SaveSettings(_s);
            _root = Store.Root(_s);
            FolderBox.Text = _root;
            _cur = null;
            Reload();
            NewDraft();
        };
    }

    private void ColorField(TextBox box, Action<string> set)
    {
        var v = box.Text?.Trim() ?? "";
        if (v.Length > 0 && !Themes.TryParse(v, out _)) return;
        set(v);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Themes.Apply(_s);
        BodyBox.FontFamily = TitleBox.FontFamily = Themes.FontFor(_s.Font);
        LightBtn.Classes.Set("primary", _s.Mode != "Dark");
        DarkBtn.Classes.Set("primary", _s.Mode == "Dark");
        foreach (var b in Swatches.Children.OfType<Button>())
        {
            bool on = (string?)b.Tag == _s.Accent;
            b.BorderThickness = new Thickness(on ? 2 : 0);
            b.BorderBrush = (IBrush?)Application.Current!.Resources["TextBrush"];
        }
        OpacityLabel.Text = $"Window opacity · {Math.Round(_s.Opacity * 100)}%";
        FontSizeLabel.Text = $"Text size · {_s.FontSize:0}";
        Store.SaveSettings(_s);
    }
}
