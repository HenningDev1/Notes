using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Notes;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Store.LoadSettings();
            Themes.Apply(settings);
            var icon = Logo.CreateIcon();
            var window = new MainWindow(settings) { Icon = icon };

            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open Notes");
            open.Click += (_, _) => window.ShowCapture(newNote: false);
            var add = new NativeMenuItem("New note");
            add.Click += (_, _) => window.ShowCapture(newNote: true);
            var quit = new NativeMenuItem("Quit");
            quit.Click += (_, _) => { window.Flush(); desktop.Shutdown(); };
            menu.Items.Add(open);
            menu.Items.Add(add);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            var tray = new TrayIcon { Icon = icon, ToolTipText = "Notes", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => window.Toggle();
            TrayIcon.SetIcons(this, new TrayIcons { tray });

            window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
