using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopIconsToggle;

/// <summary>
/// Autostart of the MSIX (Microsoft Store) build, through the StartupTask declared in its
/// manifest. A packaged app cannot use the Run key: its HKCU writes are virtualized, and Store
/// policy requires the StartupTask anyway.
///
/// WinRT is called through raw COM vtables so the exe keeps needing no projection assembly.
/// Slot numbers come from the Windows SDK headers (windows.applicationmodel.h, asyncinfo.h):
/// IUnknown and IInspectable take slots 0-5.
/// </summary>
internal sealed unsafe class StartupTaskAutostart : IAutostart
{
    /// <summary>Must match the TaskId of the StartupTask in packaging/AppxManifest.xml.</summary>
    public const string TaskId = "DeskToggleStartup";

    private enum State
    {
        Disabled = 0,
        DisabledByUser = 1,
        Enabled = 2,
        DisabledByPolicy = 3,
        EnabledByPolicy = 4,
    }

    private const int AppModelErrorNoPackage = 15700;
    private const int RoInitMultithreaded = 1;
    private const int AsyncStarted = 0;
    private const int AsyncCompleted = 1;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private static readonly Guid IidStartupTaskStatics = new("EE5B60BD-A148-41A7-B26E-E8B88A1E62F8");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    /// <summary>True when the process runs with a package identity (installed from an MSIX).</summary>
    public static bool IsPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(&length, null) != AppModelErrorNoPackage;
    }

    public bool IsEnabled() => Run(GetState) is State.Enabled or State.EnabledByPolicy;

    public bool Toggle()
    {
        State? state = Run(task => GetState(task) switch
        {
            State.Enabled => Disable(task),
            State.Disabled => RequestEnable(task),
            var other => other,
        });

        // Once the user has turned the task off in Settings or Task Manager, only they can turn
        // it back on: take them there.
        if (state == State.DisabledByUser)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No Settings app: the menu item simply stays unchecked.
            }
        }

        return state is State.Enabled or State.EnabledByPolicy;
    }

    // WinRT asynchronous operations are polled on a dedicated MTA thread, so the UI thread's
    // apartment never has to pump anything for them. Returns null on any failure.
    private static State? Run(Func<IntPtr, State> action)
    {
        State? result = null;
        var thread = new Thread(() =>
        {
            int init = RoInitialize(RoInitMultithreaded);
            IntPtr task = IntPtr.Zero;
            try
            {
                task = GetTask();
                result = action(task);
            }
            catch (Exception)
            {
                // Never let a tray-menu click take the app down from a worker thread: the menu
                // item simply stays unchecked.
            }
            finally
            {
                if (task != IntPtr.Zero) Release(task);
                if (init >= 0) RoUninitialize();  // S_OK and S_FALSE both need balancing
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(10)) ? result : null;
    }

    // StartupTask.GetAsync(TaskId) -> IStartupTask*
    private static IntPtr GetTask()
    {
        IntPtr statics;
        Guid iid = IidStartupTaskStatics;
        using (var className = new HString("Windows.ApplicationModel.StartupTask"))
        {
            Check(RoGetActivationFactory(className.Handle, &iid, &statics));
        }

        try
        {
            IntPtr operation;
            using (var id = new HString(TaskId))
            {
                Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 7))(statics, id.Handle, &operation));
            }

            try
            {
                Wait(operation);
                IntPtr task;
                Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(operation, 8))(operation, &task));
                return task;
            }
            finally
            {
                Release(operation);
            }
        }
        finally
        {
            Release(statics);
        }
    }

    // IStartupTask::get_State
    private static State GetState(IntPtr task)
    {
        int state;
        Check(((delegate* unmanaged<IntPtr, int*, int>)Slot(task, 8))(task, &state));
        return (State)state;
    }

    // IStartupTask::Disable
    private static State Disable(IntPtr task)
    {
        Check(((delegate* unmanaged<IntPtr, int>)Slot(task, 7))(task));
        return State.Disabled;
    }

    // IStartupTask::RequestEnableAsync -> IAsyncOperation<StartupTaskState>
    private static State RequestEnable(IntPtr task)
    {
        IntPtr operation;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(task, 6))(task, &operation));
        try
        {
            Wait(operation);
            int state;
            Check(((delegate* unmanaged<IntPtr, int*, int>)Slot(operation, 8))(operation, &state));
            return (State)state;
        }
        finally
        {
            Release(operation);
        }
    }

    // Polls IAsyncInfo::get_Status until the operation leaves the Started state.
    private static void Wait(IntPtr operation)
    {
        IntPtr info;
        Guid iid = IidAsyncInfo;
        Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(operation, 0))(operation, &iid, &info));
        try
        {
            int status;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                Check(((delegate* unmanaged<IntPtr, int*, int>)Slot(info, 7))(info, &status));
                if (status != AsyncStarted) break;
                if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException();
                Thread.Sleep(5);
            }

            if (status != AsyncCompleted)
            {
                int error = 0;
                ((delegate* unmanaged<IntPtr, int*, int>)Slot(info, 8))(info, &error);
                Check(error < 0 ? error : ErrorCancelled);
            }
        }
        finally
        {
            Release(info);
        }
    }

    private static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

    private static void Release(IntPtr comObject) => ((delegate* unmanaged<IntPtr, uint>)Slot(comObject, 2))(comObject);

    private static void Check(int hr)
    {
        if (hr < 0) throw new COMException("StartupTask call failed.", hr);
    }

    private readonly struct HString : IDisposable
    {
        public readonly IntPtr Handle;

        public HString(string value)
        {
            IntPtr handle;
            fixed (char* chars = value)
            {
                Check(WindowsCreateString(chars, (uint)value.Length, &handle));
            }

            Handle = handle;
        }

        public void Dispose() => WindowsDeleteString(Handle);
    }

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(uint* packageFullNameLength, char* packageFullName);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, IntPtr* factory);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(char* sourceString, uint length, IntPtr* hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
