using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopIconsToggle.Native;

namespace DesktopIconsToggle;

internal static class Program
{
    private const nuint TooltipTimerId = 1;

    // Tried in order the first time the app runs, and whenever the user has not chosen a
    // combination themselves.
    //
    // Deliberately no Windows-key combination here. The shell processes Win+<key> through
    // its own keyboard hook, ahead of registered-hotkey dispatch, so RegisterHotKey can
    // report success for a combination that then never fires — the registration is held
    // but the keystroke is consumed upstream. That was observed with Win+Maj+D: registered
    // (a second RegisterHotKey from another process was refused with
    // ERROR_HOTKEY_ALREADY_REGISTERED) yet no WM_HOTKEY ever arrived, while posting
    // WM_HOTKEY by hand toggled correctly. Ctrl+Alt combinations are dispatched normally.
    private static readonly (uint Modifiers, uint VirtualKey)[] HotkeyFallbacks =
    [
        (Win32.MOD_CONTROL | Win32.MOD_ALT, VirtualKeyD),
        (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT, VirtualKeyD),
        (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT, VirtualKeyF9),
    ];

    private const uint VirtualKeyD = 0x44;
    private const uint VirtualKeyF9 = 0x78;

    // Explorer needs ~150 ms to settle a posted toggle (see the reconciliation timer
    // below); 300 ms leaves comfortable margin so a second press can never land while the
    // first is still in flight.
    private static readonly ToggleThrottle _toggleThrottle = new(300);

    private static AppWindow? _window;
    private static TrayIcon? _tray;
    private static DesktopWatcher? _watcher;
    private static IAutostart? _autostart;
    private static Settings? _settings;
    private static TaskbarHider? _taskbar;
    private static HotKey? _hotKey;
    private static uint _taskbarCreatedMessage;
    private static IntPtr _windowHandle;
    private static int _shutdownStarted;

    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\DesktopIconsToggle", out bool isNew);
        if (!isNew)
        {
            return 0;
        }

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

        var settings = new Settings(new RegistryValueStore(Settings.SubKey));
        settings.Load();
        _settings = settings;

        // Crash recovery for the icons. The app clears this marker on every clean exit, so
        // finding it set means the previous session was killed while they were hidden.
        // Explorer persists that state, so nothing else would ever bring them back.
        if (settings.IconsHiddenByUs)
        {
            IconToggler.EnsureVisible();
            settings.IconsHiddenByUs = false;
            settings.Save();
        }

        // The Microsoft Store (MSIX) build starts through the StartupTask of its manifest: a
        // packaged app's Run-key write would only land in its private, virtualized registry.
        _autostart = StartupTaskAutostart.IsPackaged()
            ? new StartupTaskAutostart()
            : new Autostart(new RegistryValueStore(RegistryValueStore.RunSubKey), exePath);

        var taskbar = new TaskbarHider();
        _taskbar = taskbar;

        // Crash recovery, before capturing the original state: if a previous session was
        // killed with the taskbar auto-hidden by us, that auto-hide is ours, not the
        // user's. Undo it and clear the marker before CaptureOriginalState runs, or the
        // capture below would record our own leftover state as the user's original.
        if (settings.TaskbarHiddenByUs)
        {
            taskbar.SetAutoHide(false);
            settings.TaskbarHiddenByUs = false;
            settings.Save();
        }

        taskbar.CaptureOriginalState();

        _taskbarCreatedMessage = Win32.RegisterWindowMessageW("TaskbarCreated");

        // The manifest declares comctl32 v6, which switches every standard control (the
        // hotkey dialog's buttons among them) to the current visual style instead of the
        // pre-XP default. This call is what actually initialises that version.
        var initControls = new Win32.INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<Win32.INITCOMMONCONTROLSEX>(),
            dwICC = Win32.ICC_STANDARD_CLASSES,
        };
        Win32.InitCommonControlsEx(ref initControls);

        var window = new AppWindow();
        _window = window;
        _windowHandle = window.Handle;
        window.MessageReceived += OnMessage;

        // Locals, not the nullable fields: a lambda capturing a nullable field
        // loses its flow analysis and trips CS8602 under TreatWarningsAsErrors.
        var tray = new TrayIcon(window.Handle);
        _tray = tray;
        tray.CommandChosen += OnCommand;

        // So the tray icon itself already shows the right state — active toggle on, or
        // outlined toggle off — at the very first paint, rather than whatever placeholder
        // art the constructor started with.
        tray.SetIconState(IconToggler.AreIconsVisible());

        var watcher = new DesktopWatcher();
        _watcher = watcher;
        watcher.Changed += isDesktop => tray.SetVisible(isDesktop);
        bool watcherStarted = watcher.Start();

        if (!watcherStarted)
        {
            // Without the foreground hook the icon would never be added after its
            // initial state, so pin it visible: the menu, and Quitter, must stay reachable.
            tray.SetVisible(true);
        }

        var hotKey = new HotKey(window.Handle);
        _hotKey = hotKey;

        RegisterStartupHotkey(hotKey, settings);

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        window.RunMessageLoop();

        Shutdown();
        return 0;
    }

    /// <summary>
    /// Registers the hotkey for the very first message loop iteration. A combination the
    /// user picked themselves (<see cref="Settings.HotkeyConfigured"/>) is registered as-is
    /// and reported on failure, never silently swapped. Otherwise the fallback chain is
    /// walked and the first combination Windows grants is persisted, so the tray menu
    /// reflects what is actually active.
    /// </summary>
    private static void RegisterStartupHotkey(HotKey hotKey, Settings settings)
    {
        if (settings.HotkeyConfigured)
        {
            if (!hotKey.Register(settings.HotkeyModifiers, settings.HotkeyVirtualKey))
            {
                _tray?.UpdateTooltip("Icônes du Bureau — raccourci clavier indisponible");
                return;
            }

            RefreshTooltip();
            return;
        }

        foreach ((uint modifiers, uint virtualKey) in HotkeyFallbacks)
        {
            if (hotKey.Register(modifiers, virtualKey))
            {
                settings.HotkeyModifiers = modifiers;
                settings.HotkeyVirtualKey = virtualKey;
                settings.Save();
                RefreshTooltip();
                return;
            }
        }

        _tray?.UpdateTooltip("Icônes du Bureau — raccourci clavier indisponible");
    }

    private static bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_TIMER && (nuint)wParam == TooltipTimerId)
        {
            Win32.KillTimer(_windowHandle, TooltipTimerId);
            ReconcileWithDesktop();
            return true;
        }

        if (msg == Win32.WM_HOTKEY && (int)wParam == HotKey.Id)
        {
            ToggleAll();
            return true;
        }

        if (msg == Win32.WM_APP_TRAY)
        {
            int mouseMessage = (int)lParam;
            if (mouseMessage == Win32.WM_LBUTTONUP)
            {
                ToggleAll();
            }
            else if (mouseMessage == Win32.WM_RBUTTONUP)
            {
                // A transient holder at startup — another app launching, or a previous
                // instance still exiting — would otherwise cost the hotkey for the whole
                // session, with the dialog as the only way back. Retry when the user looks.
                if (_hotKey is not null && !_hotKey.IsRegistered)
                {
                    _hotKey.Register(_settings!.HotkeyModifiers, _settings.HotkeyVirtualKey);
                }

                _tray?.ShowMenu(new TrayMenuState(
                    IconToggler.AreIconsVisible(),
                    _settings!.HideTaskbar,
                    _hotKey!.IsRegistered
                        ? HotkeyFormatter.Format(_hotKey.Modifiers, _hotKey.VirtualKey)
                        : "aucun",
                    _autostart?.IsEnabled() ?? false));
            }

            return true;
        }

        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            _tray?.Readd();

            // Explorer has (re)started. If we never got a trustworthy reading — we were
            // launched at logon before the shell was up — take it now.
            if (_taskbar is not null && !_taskbar.HasOriginalState)
            {
                _taskbar.CaptureOriginalState();
            }

            return true;
        }

        // wParam is FALSE when a previously announced session end was cancelled;
        // the app must keep running in that case.
        if (msg == Win32.WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            // Restore synchronously: the system may terminate the process at any point
            // after this returns, so deferring the restore to the message loop is not safe.
            Shutdown();
            return true;
        }

        if (msg == Win32.WM_SETTINGCHANGE)
        {
            // lParam points to a string naming the setting that changed, but is not
            // guaranteed to be set — Windows sends WM_SETTINGCHANGE for many other kinds
            // of change too, with a null lParam being the common case for those.
            string? setting = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
            if (setting == "ImmersiveColorSet")
            {
                _tray?.OnSettingsChanged();
            }

            // Not ours alone to swallow: other listeners (and DefWindowProc) also care
            // about this broadcast, so let it keep propagating.
            return false;
        }

        return false;
    }

    private static void OnCommand(int command)
    {
        switch (command)
        {
            case TrayIcon.CmdToggle:
                ToggleAll();
                break;

            case TrayIcon.CmdAutostart:
                _autostart?.Toggle();
                break;

            case TrayIcon.CmdHideTaskbar:
                _settings!.HideTaskbar = !_settings.HideTaskbar;
                // Apply immediately so the checkbox matches what the user sees, rather
                // than only taking effect at the next toggle.
                ApplyTaskbarState(_settings.HideTaskbar && !IconToggler.AreIconsVisible());
                _settings.Save();
                break;

            case TrayIcon.CmdChangeHotkey:
                ChangeHotkey();
                break;

            case TrayIcon.CmdQuit:
                Win32.PostQuitMessage(0);
                break;
        }
    }

    private static void ToggleAll()
    {
        // Refuse before touching the marker, IconToggler or the taskbar: a press that
        // arrives while the previous toggle is still settling must have no side effect
        // at all, not merely a suppressed one.
        if (!_toggleThrottle.ShouldAccept(Environment.TickCount64))
        {
            return;
        }

        bool willHide = IconToggler.AreIconsVisible();

        // Claim them before posting. If we are killed between the post and the timer
        // below, the next launch finds the marker set and restores — a needless restore
        // is harmless, a missed one is not.
        _settings!.IconsHiddenByUs = true;
        _settings.Save();

        if (!IconToggler.Toggle())
        {
            _tray?.UpdateTooltip("Icônes du Bureau — bureau indisponible");
            return;
        }

        // Optimistic, immediate feedback; ReconcileWithDesktop corrects it once Explorer
        // has actually settled.
        _tray?.SetIconState(!willHide);

        // Apply the taskbar immediately so it tracks the icons without a visible lag.
        // The timer reconciles both against what Explorer actually settled on.
        if (_settings.HideTaskbar)
        {
            ApplyTaskbarState(willHide);
            _settings.Save();
        }

        // Explorer needs a moment before the window state reflects the posted toggle.
        // Sleeping here would block the message loop that also services the hotkey and
        // the tray icon, so a timer is used instead of a wait.
        if (_windowHandle != IntPtr.Zero)
        {
            Win32.SetTimer(_windowHandle, TooltipTimerId, 150, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Replaces the intent recorded before the toggle with the state Explorer actually
    /// settled on. Two toggles posted inside Explorer's latency window would otherwise
    /// leave the marker, the taskbar and the tooltip all describing the opposite result.
    /// </summary>
    private static void ReconcileWithDesktop()
    {
        bool iconsHidden = !IconToggler.AreIconsVisible();

        _settings!.IconsHiddenByUs = iconsHidden;

        if (_settings.HideTaskbar)
        {
            ApplyTaskbarState(iconsHidden);
        }

        _settings.Save();
        RefreshTooltip();
        _tray?.SetIconState(!iconsHidden);
    }

    private static void ApplyTaskbarState(bool hidden)
    {
        // Acting on an original state we never trustworthily read is exactly the danger
        // C3 guards against: refuse until CaptureOriginalState has succeeded (including a
        // retry once TaskbarCreated fires).
        if (!_taskbar!.HasOriginalState)
        {
            return;
        }

        (bool autoHide, bool ownedByUs) = TaskbarPolicy.Decide(hidden, _taskbar.OriginalAutoHide);

        // When restoring, go back to whatever the user had before we started —
        // someone who already used auto-hide must keep it.
        _taskbar.SetAutoHide(autoHide);

        // Only claim responsibility when we actually changed it from their original.
        // Persisting is the caller's job, so a single toggle writes the registry once.
        _settings!.TaskbarHiddenByUs = ownedByUs;
    }

    private static void ChangeHotkey()
    {
        uint previousModifiers = _hotKey!.Modifiers;
        uint previousVirtualKey = _hotKey.VirtualKey;

        // The user must be able to re-pick the combination they already hold.
        _hotKey.Unregister();

        if (HotkeyCapture.TryCapture(_windowHandle, out uint modifiers, out uint virtualKey)
            && _hotKey.Register(modifiers, virtualKey))
        {
            _settings!.HotkeyModifiers = modifiers;
            _settings.HotkeyVirtualKey = virtualKey;
            _settings.HotkeyConfigured = true;
            _settings.Save();
            RefreshTooltip();
            return;
        }

        // Cancelled, or the combination stopped being available between the dialog's test
        // and now: put the previous one back rather than leaving none at all.
        if (!_hotKey.Register(previousModifiers, previousVirtualKey))
        {
            _tray?.UpdateTooltip("Icônes du Bureau — raccourci clavier indisponible");
        }
    }

    private static void RefreshTooltip()
        => _tray?.UpdateTooltip(IconToggler.AreIconsVisible()
            ? "Icônes du Bureau — affichées"
            : "Icônes du Bureau — masquées");

    private static void Shutdown()
    {
        // Both the message loop's return and the ProcessExit handler call this.
        // Without the guard, EnsureVisible runs twice milliseconds apart — and because
        // Explorer needs a moment to reflect a posted toggle, the second call would
        // still read "hidden" and toggle again, re-hiding what the first call restored.
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        bool iconsRestored = IconToggler.EnsureVisible();   // never leave the user without icons

        _hotKey?.Dispose();
        bool taskbarRestored = _taskbar?.RestoreOriginal() ?? true;

        if (_settings is not null)
        {
            // Only clear a marker whose restore we could confirm. A marker left set costs
            // nothing — the next launch re-runs the recovery, which is idempotent — while
            // clearing it wrongly deletes the only record that anything needs repairing.
            if (iconsRestored)
            {
                _settings.IconsHiddenByUs = false;
            }

            if (taskbarRestored)
            {
                _settings.TaskbarHiddenByUs = false;
            }

            _settings.Save();
        }

        if (_windowHandle != IntPtr.Zero)
        {
            Win32.KillTimer(_windowHandle, TooltipTimerId);
        }

        _tray?.Dispose();
        _watcher?.Dispose();
        _window?.Dispose();
        _tray = null;
        _watcher = null;
        _window = null;
        _windowHandle = IntPtr.Zero;
        _hotKey = null;
        _taskbar = null;
    }
}
