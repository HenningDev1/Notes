# Notes

Quick-capture notes for Windows, built with C# and Avalonia 11.

- **Global hotkey** (default `Ctrl+Alt+N`, change it in Settings) opens a fresh note; `Esc` hides it. Tray icon menu: Open, New note, Quit.
- **Any window shape**: drag any edge or corner. It works from a thin full-height strip up to full screen,
  and the layout adapts (the sidebar turns into a dropdown when the window is narrow). Presets: Slim left/right,
  Strip top/bottom, Compact, Normal, Fill screen. Double-click the title bar to switch between slim and normal.
- **Formats**: md, txt, html, json, csv, log. Notes are plain files in `Documents\Notes\<Category>\`, and you can
  change the folder in Settings. You can export a copy anywhere.
- **Categories** are subfolders. Make one with the folder+ button next to the category filter.
- **Password protection**: AES-256-GCM, with the key derived using PBKDF2-SHA256 (310k iterations). Protected notes are saved
  as `name.md.locked`. Lock again with `Ctrl+L`. If you forget the password, the note can't be recovered.
- **Themes**: Light/Dark, accent presets or a custom hex color, custom background and text colors, window opacity,
  font (Inter by default) and text size.
- Deleted notes go to the Windows Recycle Bin.
- Regenerate the exe icon after changing the logo: `dotnet run -- --export-icon app.ico`

Install (adds Notes to the Start menu and Windows Search, no admin needed):

    powershell -ExecutionPolicy Bypass -File build-installer.ps1
    dist\NotesSetup.exe

Uninstall from Settings > Apps. Your notes and settings are kept.

Build and run from source:

    dotnet run -c Release
