using EasyHook;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TaskBar2.TrayHook.Injectee;

public sealed class TrayIconHookEntryPoint : IEntryPoint
{
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");
    private static readonly Guid IidTaskbarList4 = new("C43DC798-95D1-4BEA-9030-BB99E2983A1A");
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifState = 0x00000008;
    private const uint NifGuid = 0x00000020;
    private const uint NisHidden = 0x00000001;
    private const uint ClsctxAll = 0x17;
    private const uint CoinitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly ConcurrentQueue<string> PendingMessages = new();
    private static readonly AutoResetEvent PendingSignal = new(false);
    private static readonly ConcurrentDictionary<string, int> Versions = new();

    private static ShellNotifyIconDelegate? _shellNotifyIconWDelegate;
    private static ShellNotifyIconDelegate? _shellNotifyIconADelegate;
    private static CoCreateInstanceDelegate? _coCreateInstanceDelegate;
    private static SetProgressValueDelegate? _setProgressValueDelegate;
    private static SetProgressStateDelegate? _setProgressStateDelegate;
    private static SetOverlayIconDelegate? _setOverlayIconDelegate;
    private static SetProgressValueDelegate? _originalSetProgressValue;
    private static SetProgressStateDelegate? _originalSetProgressState;
    private static SetOverlayIconDelegate? _originalSetOverlayIcon;
    private static string _pipeName = "";
    private static readonly ConcurrentDictionary<IntPtr, LocalHook> TaskbarMethodHooks = [];

    private LocalHook? _shellNotifyIconWHook;
    private LocalHook? _shellNotifyIconAHook;
    private LocalHook? _coCreateInstanceHook;

    public TrayIconHookEntryPoint(RemoteHooking.IContext context, string pipeName, string stopEventName, string globalStopEventName)
    {
        _pipeName = pipeName;
    }

    public void Run(RemoteHooking.IContext context, string pipeName, string stopEventName, string globalStopEventName)
    {
        _pipeName = pipeName;
        InjecteeLog.Write($"Injectee Run entered. ProcessId={Process.GetCurrentProcess().Id} ProcessName={Process.GetCurrentProcess().ProcessName} Pipe={pipeName}");
        using var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
        using var globalStopEvent = EventWaitHandle.OpenExisting(globalStopEventName);
        using var writerStop = new ManualResetEvent(false);
        var writer = new Thread(() => PipeWriterLoop(writerStop))
        {
            IsBackground = true,
            Name = "TaskBar2 tray hook pipe writer"
        };
        writer.Start();

        try
        {
            InstallHooks();
            ExplorerTaskbarSnapshotCapture.StartIfExplorer();
            var stopHandles = new WaitHandle[] { stopEvent, globalStopEvent };
            while (WaitHandle.WaitAny(stopHandles, 1000) == WaitHandle.WaitTimeout)
            {
            }
        }
        finally
        {
            ExplorerTaskbarSnapshotCapture.Stop();
            DisposeHook(_shellNotifyIconWHook);
            DisposeHook(_shellNotifyIconAHook);
            DisposeHook(_coCreateInstanceHook);
            foreach (var hook in TaskbarMethodHooks.Values)
            {
                DisposeHook(hook);
            }

            TaskbarMethodHooks.Clear();
            try
            {
                LocalHook.Release();
            }
            catch
            {
            }

            writerStop.Set();
            PendingSignal.Set();
            writer.Join(1000);
        }
    }

    private static void DisposeHook(LocalHook? hook)
    {
        try
        {
            hook?.Dispose();
        }
        catch
        {
        }
    }

    private void InstallHooks()
    {
        _shellNotifyIconWDelegate = ShellNotifyIconWHook;
        _shellNotifyIconADelegate = ShellNotifyIconAHook;
        _coCreateInstanceDelegate = CoCreateInstanceHook;
        _setProgressValueDelegate = SetProgressValueHook;
        _setProgressStateDelegate = SetProgressStateHook;
        _setOverlayIconDelegate = SetOverlayIconHook;

        _shellNotifyIconWHook = LocalHook.Create(
            LocalHook.GetProcAddress("shell32.dll", "Shell_NotifyIconW"),
            _shellNotifyIconWDelegate,
            this);
        _shellNotifyIconAHook = LocalHook.Create(
            LocalHook.GetProcAddress("shell32.dll", "Shell_NotifyIconA"),
            _shellNotifyIconADelegate,
            this);
        _coCreateInstanceHook = LocalHook.Create(
            LocalHook.GetProcAddress("ole32.dll", "CoCreateInstance"),
            _coCreateInstanceDelegate,
            this);

        _shellNotifyIconWHook.ThreadACL.SetExclusiveACL(new[] { 0 });
        _shellNotifyIconAHook.ThreadACL.SetExclusiveACL(new[] { 0 });
        _coCreateInstanceHook.ThreadACL.SetExclusiveACL(new[] { 0 });

        InstallTaskbarListHooksFromLocalInstance();
    }

    private static bool ShellNotifyIconWHook(uint message, IntPtr data)
    {
        var result = ShellNotifyIconW(message, data);
        Capture(message, data, unicode: true, result);
        return result;
    }

    private static bool ShellNotifyIconAHook(uint message, IntPtr data)
    {
        var result = ShellNotifyIconA(message, data);
        Capture(message, data, unicode: false, result);
        return result;
    }

    private static int CoCreateInstanceHook(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv)
    {
        var result = CoCreateInstance(ref rclsid, pUnkOuter, dwClsContext, ref riid, out ppv);
        if (result >= 0 &&
            ppv != IntPtr.Zero &&
            rclsid == ClsidTaskbarList &&
            (riid == IidTaskbarList3 || riid == IidTaskbarList4))
        {
            TryInstallTaskbarListHooks(ppv);
        }

        return result;
    }

    private static int SetProgressValueHook(IntPtr taskbarList, IntPtr hwnd, ulong completed, ulong total)
    {
        var result = _originalSetProgressValue is null
            ? unchecked((int)0x80004005)
            : _originalSetProgressValue(taskbarList, hwnd, completed, total);
        if (result >= 0)
        {
            CaptureTaskbarState(new TaskbarStateSnapshot
            {
                Operation = "SetProgressValue",
                Hwnd = hwnd,
                ProgressCompleted = completed,
                ProgressTotal = total
            });
        }

        return result;
    }

    private static int SetProgressStateHook(IntPtr taskbarList, IntPtr hwnd, uint state)
    {
        var result = _originalSetProgressState is null
            ? unchecked((int)0x80004005)
            : _originalSetProgressState(taskbarList, hwnd, state);
        if (result >= 0)
        {
            CaptureTaskbarState(new TaskbarStateSnapshot
            {
                Operation = "SetProgressState",
                Hwnd = hwnd,
                ProgressState = state
            });
        }

        return result;
    }

    private static int SetOverlayIconHook(IntPtr taskbarList, IntPtr hwnd, IntPtr icon, IntPtr description)
    {
        var result = _originalSetOverlayIcon is null
            ? unchecked((int)0x80004005)
            : _originalSetOverlayIcon(taskbarList, hwnd, icon, description);
        if (result >= 0)
        {
            CaptureTaskbarState(new TaskbarStateSnapshot
            {
                Operation = "SetOverlayIcon",
                Hwnd = hwnd,
                OverlayPngBase64 = icon == IntPtr.Zero ? null : IconEncoder.ToPngBase64(icon),
                OverlayDescription = description == IntPtr.Zero
                    ? ""
                    : Marshal.PtrToStringUni(description) ?? ""
            });
        }

        return result;
    }

    private static void TryInstallTaskbarListHooks(IntPtr taskbarList)
    {
        try
        {
            var vtable = Marshal.ReadIntPtr(taskbarList);
            InstallSetProgressValueHook(Marshal.ReadIntPtr(vtable, IntPtr.Size * 9));
            InstallSetProgressStateHook(Marshal.ReadIntPtr(vtable, IntPtr.Size * 10));
            InstallSetOverlayIconHook(Marshal.ReadIntPtr(vtable, IntPtr.Size * 18));
        }
        catch (Exception exception)
        {
            InjecteeLog.Write($"Taskbar method hook install failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void InstallTaskbarListHooksFromLocalInstance()
    {
        var comInitialized = false;
        var taskbarList = IntPtr.Zero;
        try
        {
            var initializeResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
            comInitialized = initializeResult >= 0;
            if (initializeResult < 0 && initializeResult != RpcEChangedMode)
            {
                InjecteeLog.Write($"Taskbar hook bootstrap skipped: CoInitializeEx=0x{initializeResult:X8}");
                return;
            }

            var clsid = ClsidTaskbarList;
            var iid = IidTaskbarList3;
            var result = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxAll, ref iid, out taskbarList);
            if (result < 0 || taskbarList == IntPtr.Zero)
            {
                InjecteeLog.Write($"Taskbar hook bootstrap skipped: CoCreateInstance=0x{result:X8}");
                return;
            }

            TryInstallTaskbarListHooks(taskbarList);
            InjecteeLog.Write("Taskbar hook bootstrap installed method hooks from local ITaskbarList3 instance.");
        }
        catch (Exception exception)
        {
            InjecteeLog.Write($"Taskbar hook bootstrap failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (taskbarList != IntPtr.Zero)
            {
                Marshal.Release(taskbarList);
            }

            if (comInitialized)
            {
                CoUninitialize();
            }
        }
    }

    private static void InstallSetProgressValueHook(IntPtr address)
    {
        if (address == IntPtr.Zero || TaskbarMethodHooks.ContainsKey(address))
        {
            return;
        }

        _originalSetProgressValue ??= Marshal.GetDelegateForFunctionPointer<SetProgressValueDelegate>(address);
        var hook = LocalHook.Create(address, _setProgressValueDelegate, null);
        hook.ThreadACL.SetExclusiveACL(new[] { 0 });
        TaskbarMethodHooks.TryAdd(address, hook);
    }

    private static void InstallSetProgressStateHook(IntPtr address)
    {
        if (address == IntPtr.Zero || TaskbarMethodHooks.ContainsKey(address))
        {
            return;
        }

        _originalSetProgressState ??= Marshal.GetDelegateForFunctionPointer<SetProgressStateDelegate>(address);
        var hook = LocalHook.Create(address, _setProgressStateDelegate, null);
        hook.ThreadACL.SetExclusiveACL(new[] { 0 });
        TaskbarMethodHooks.TryAdd(address, hook);
    }

    private static void InstallSetOverlayIconHook(IntPtr address)
    {
        if (address == IntPtr.Zero || TaskbarMethodHooks.ContainsKey(address))
        {
            return;
        }

        _originalSetOverlayIcon ??= Marshal.GetDelegateForFunctionPointer<SetOverlayIconDelegate>(address);
        var hook = LocalHook.Create(address, _setOverlayIconDelegate, null);
        hook.ThreadACL.SetExclusiveACL(new[] { 0 });
        TaskbarMethodHooks.TryAdd(address, hook);
    }

    private static void Capture(uint shellMessage, IntPtr data, bool unicode, bool shellResult)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        if (!shellResult && shellMessage != NimAdd && shellMessage != NimModify && shellMessage != NimSetVersion)
        {
            return;
        }

        try
        {
            var snapshot = NotifyIconDataReader.Read(shellMessage, data, unicode);
            if (snapshot is null)
            {
                return;
            }

            var identity = snapshot.Identity;
            if (shellMessage == NimSetVersion)
            {
                Versions[identity] = snapshot.NotificationVersion;
            }
            else if (Versions.TryGetValue(identity, out var version))
            {
                snapshot.NotificationVersion = version;
            }

            PendingMessages.Enqueue(snapshot.ToJson());
            PendingSignal.Set();
        }
        catch
        {
            // Do not let hook-side diagnostics destabilize the target process.
        }
    }

    private static void CaptureTaskbarState(TaskbarStateSnapshot snapshot)
    {
        try
        {
            snapshot.SourceProcessId = ProcessIdentity.Id;
            snapshot.SourceProcessName = ProcessIdentity.Name;
            snapshot.SourceProcessPath = ProcessIdentity.Path;
            PendingMessages.Enqueue(snapshot.ToJson());
            PendingSignal.Set();
        }
        catch
        {
        }
    }

    internal static void EnqueueMessage(string message)
    {
        PendingMessages.Enqueue(message);
        PendingSignal.Set();
    }

    private static void PipeWriterLoop(WaitHandle stopEvent)
    {
        while (!stopEvent.WaitOne(0))
        {
            if (!PendingSignal.WaitOne(500) && PendingMessages.IsEmpty)
            {
                continue;
            }

            while (PendingMessages.TryDequeue(out var message))
            {
                TrySend(message);
            }
        }
    }

    private static void TrySend(string message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.None);
            pipe.Connect(250);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            writer.WriteLine(message);
        }
        catch
        {
        }
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint dwMessage, IntPtr lpData);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconA", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconA(uint dwMessage, IntPtr lpData);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ShellNotifyIconDelegate(uint dwMessage, IntPtr lpData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CoCreateInstanceDelegate(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetProgressValueDelegate(
        IntPtr taskbarList,
        IntPtr hwnd,
        ulong completed,
        ulong total);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetProgressStateDelegate(
        IntPtr taskbarList,
        IntPtr hwnd,
        uint state);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetOverlayIconDelegate(
        IntPtr taskbarList,
        IntPtr hwnd,
        IntPtr icon,
        IntPtr description);

    private sealed class NotifyIconSnapshot
    {
        public uint ShellMessage { get; set; }

        public IntPtr OwnerHwnd { get; set; }

        public int IconId { get; set; }

        public Guid Guid { get; set; }

        public uint Flags { get; set; }

        public int CallbackMessage { get; set; }

        public int NotificationVersion { get; set; }

        public string ToolTip { get; set; } = "";

        public string? IconPngBase64 { get; set; }

        public bool Hidden { get; set; }

        public bool HiddenStateKnown { get; set; }

        public int SourceProcessId { get; set; }

        public string SourceProcessName { get; set; } = "";

        public string SourceProcessPath { get; set; } = "";

        public string Identity => Guid != Guid.Empty
            ? $"guid:{Guid:D}"
            : $"hwnd:{OwnerHwnd.ToInt64():X}:{IconId}";

        public string ToJson()
        {
            var builder = new StringBuilder(1024);
            builder.Append('{');
            Append(builder, "protocolVersion", "1", quoteValue: false);
            Append(builder, "operation", OperationName(ShellMessage));
            Append(builder, "shellMessage", ((int)ShellMessage).ToString(), quoteValue: false);
            Append(builder, "ownerHwnd", OwnerHwnd.ToInt64().ToString(), quoteValue: false);
            Append(builder, "iconId", IconId.ToString(), quoteValue: false);
            Append(builder, "guid", Guid == Guid.Empty ? "" : Guid.ToString("D"));
            Append(builder, "callbackMessage", CallbackMessage.ToString(), quoteValue: false);
            Append(builder, "notificationVersion", NotificationVersion.ToString(), quoteValue: false);
            Append(builder, "toolTip", ToolTip);
            Append(builder, "iconPngBase64", IconPngBase64 ?? "");
            Append(builder, "hidden", Hidden ? "true" : "false", quoteValue: false);
            Append(builder, "hiddenStateKnown", HiddenStateKnown ? "true" : "false", quoteValue: false);
            Append(builder, "sourceProcessId", SourceProcessId.ToString(), quoteValue: false);
            Append(builder, "sourceProcessName", SourceProcessName);
            Append(builder, "sourceProcessPath", SourceProcessPath);
            builder.Length--;
            builder.Append('}');
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string name, string value, bool quoteValue = true)
        {
            builder.Append('"');
            builder.Append(name);
            builder.Append("\":");
            if (quoteValue)
            {
                builder.Append('"');
                builder.Append(Escape(value));
                builder.Append('"');
            }
            else
            {
                builder.Append(value);
            }

            builder.Append(',');
        }

        private static string OperationName(uint shellMessage) => shellMessage switch
        {
            NimAdd => "Add",
            NimModify => "Modify",
            NimDelete => "Delete",
            NimSetVersion => "SetVersion",
            _ => "Unknown"
        };

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(character < ' '
                            ? "\\u" + ((int)character).ToString("x4")
                            : character);
                        break;
                }
            }

            return builder.ToString();
        }
    }

    private sealed class TaskbarStateSnapshot
    {
        public string Operation { get; set; } = "";

        public IntPtr Hwnd { get; set; }

        public uint ProgressState { get; set; }

        public ulong ProgressCompleted { get; set; }

        public ulong ProgressTotal { get; set; }

        public string? OverlayPngBase64 { get; set; }

        public string OverlayDescription { get; set; } = "";

        public int SourceProcessId { get; set; }

        public string SourceProcessName { get; set; } = "";

        public string SourceProcessPath { get; set; } = "";

        public string ToJson()
        {
            var builder = new StringBuilder(1024);
            builder.Append('{');
            Append(builder, "protocolVersion", "1", quoteValue: false);
            Append(builder, "messageType", "TaskbarState");
            Append(builder, "operation", Operation);
            Append(builder, "hwnd", Hwnd.ToInt64().ToString(), quoteValue: false);
            Append(builder, "progressState", ProgressState.ToString(), quoteValue: false);
            Append(builder, "progressCompleted", ProgressCompleted.ToString(), quoteValue: false);
            Append(builder, "progressTotal", ProgressTotal.ToString(), quoteValue: false);
            Append(builder, "overlayPngBase64", OverlayPngBase64 ?? "");
            Append(builder, "overlayDescription", OverlayDescription);
            Append(builder, "sourceProcessId", SourceProcessId.ToString(), quoteValue: false);
            Append(builder, "sourceProcessName", SourceProcessName);
            Append(builder, "sourceProcessPath", SourceProcessPath);
            builder.Length--;
            builder.Append('}');
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string name, string value, bool quoteValue = true)
        {
            builder.Append('"');
            builder.Append(name);
            builder.Append("\":");
            if (quoteValue)
            {
                builder.Append('"');
                builder.Append(Escape(value));
                builder.Append('"');
            }
            else
            {
                builder.Append(value);
            }

            builder.Append(',');
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        builder.Append(character < ' '
                            ? "\\u" + ((int)character).ToString("x4")
                            : character);
                        break;
                }
            }

            return builder.ToString();
        }
    }

    private static class NotifyIconDataReader
    {
        public static NotifyIconSnapshot? Read(uint shellMessage, IntPtr data, bool unicode)
        {
            var cbSize = ReadUInt32(data, 0, 4);
            if (cbSize < 20)
            {
                return null;
            }

            var offsets = unicode ? NotifyIconDataOffsets.Unicode : NotifyIconDataOffsets.Ansi;
            var flags = ReadUInt32(data, offsets.Flags, cbSize);
            var ownerHwnd = ReadIntPtr(data, offsets.OwnerHwnd, cbSize);
            var iconId = unchecked((int)ReadUInt32(data, offsets.IconId, cbSize));
            var callbackMessage = (flags & NifMessage) != 0
                ? unchecked((int)ReadUInt32(data, offsets.CallbackMessage, cbSize))
                : 0;
            var iconHandle = ((flags & NifIcon) != 0)
                ? ReadIntPtr(data, offsets.Icon, cbSize)
                : IntPtr.Zero;
            var toolTip = (flags & NifTip) != 0
                ? ReadString(data, offsets.ToolTip, offsets.ToolTipCharacters, unicode, cbSize)
                : "";
            var state = (flags & NifState) != 0 ? ReadUInt32(data, offsets.State, cbSize) : 0;
            var stateMask = (flags & NifState) != 0 ? ReadUInt32(data, offsets.StateMask, cbSize) : 0;
            var hiddenStateKnown = (flags & NifState) != 0 && (stateMask & NisHidden) != 0;
            var notificationVersion = shellMessage == NimSetVersion
                ? unchecked((int)ReadUInt32(data, offsets.Version, cbSize))
                : 0;
            var guid = (flags & NifGuid) != 0
                ? ReadGuid(data, offsets.Guid, cbSize)
                : Guid.Empty;

            return new NotifyIconSnapshot
            {
                ShellMessage = shellMessage,
                OwnerHwnd = ownerHwnd,
                IconId = iconId,
                Guid = guid,
                Flags = flags,
                CallbackMessage = callbackMessage,
                NotificationVersion = notificationVersion,
                ToolTip = toolTip,
                IconPngBase64 = iconHandle == IntPtr.Zero ? null : IconEncoder.ToPngBase64(iconHandle),
                Hidden = hiddenStateKnown && (state & NisHidden) != 0,
                HiddenStateKnown = hiddenStateKnown,
                SourceProcessId = ProcessIdentity.Id,
                SourceProcessName = ProcessIdentity.Name,
                SourceProcessPath = ProcessIdentity.Path
            };
        }

        private static uint ReadUInt32(IntPtr data, int offset, uint cbSize) =>
            HasBytes(offset, sizeof(uint), cbSize)
                ? unchecked((uint)Marshal.ReadInt32(data, offset))
                : 0;

        private static IntPtr ReadIntPtr(IntPtr data, int offset, uint cbSize) =>
            HasBytes(offset, IntPtr.Size, cbSize)
                ? Marshal.ReadIntPtr(data, offset)
                : IntPtr.Zero;

        private static Guid ReadGuid(IntPtr data, int offset, uint cbSize) =>
            HasBytes(offset, 16, cbSize)
                ? Marshal.PtrToStructure<Guid>(IntPtr.Add(data, offset))
                : Guid.Empty;

        private static string ReadString(IntPtr data, int offset, int characters, bool unicode, uint cbSize)
        {
            var bytesPerCharacter = unicode ? 2 : 1;
            var availableCharacters = Math.Min(characters, Math.Max(0, ((int)cbSize - offset) / bytesPerCharacter));
            if (availableCharacters <= 0)
            {
                return "";
            }

            var value = unicode
                ? Marshal.PtrToStringUni(IntPtr.Add(data, offset), availableCharacters)
                : Marshal.PtrToStringAnsi(IntPtr.Add(data, offset), availableCharacters);
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var terminator = value.IndexOf('\0');
            return terminator >= 0 ? value.Substring(0, terminator) : value;
        }

        private static bool HasBytes(int offset, int length, uint cbSize) =>
            offset >= 0 && cbSize >= offset + length;
    }

    private static class ProcessIdentity
    {
        public static int Id { get; } = Process.GetCurrentProcess().Id;

        public static string Name { get; } = SafeGetName();

        public static string Path { get; } = SafeGetPath();

        private static string SafeGetName()
        {
            try
            {
                return Process.GetCurrentProcess().ProcessName;
            }
            catch
            {
                return "";
            }
        }

        private static string SafeGetPath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }
            catch
            {
                return "";
            }
        }
    }

    private sealed class NotifyIconDataOffsets
    {
        public static readonly NotifyIconDataOffsets Unicode = Create<NotifyIconDataW>();
        public static readonly NotifyIconDataOffsets Ansi = Create<NotifyIconDataA>();

        public int OwnerHwnd { get; set; }
        public int IconId { get; set; }
        public int Flags { get; set; }
        public int CallbackMessage { get; set; }
        public int Icon { get; set; }
        public int ToolTip { get; set; }
        public int ToolTipCharacters { get; set; }
        public int State { get; set; }
        public int StateMask { get; set; }
        public int Version { get; set; }
        public int Guid { get; set; }

        private static NotifyIconDataOffsets Create<T>() => new()
        {
            OwnerHwnd = Offset<T>("hWnd"),
            IconId = Offset<T>("uID"),
            Flags = Offset<T>("uFlags"),
            CallbackMessage = Offset<T>("uCallbackMessage"),
            Icon = Offset<T>("hIcon"),
            ToolTip = Offset<T>("szTip"),
            ToolTipCharacters = 128,
            State = Offset<T>("dwState"),
            StateMask = Offset<T>("dwStateMask"),
            Version = Offset<T>("uVersion"),
            Guid = Offset<T>("guidItem")
        };

        private static int Offset<T>(string fieldName) => (int)Marshal.OffsetOf(typeof(T), fieldName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconDataW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NotifyIconDataA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private static class IconEncoder
    {
        private const int DiNormal = 0x0003;

        public static string? ToPngBase64(IntPtr iconHandle)
        {
            var copy = CopyIcon(iconHandle);
            if (copy == IntPtr.Zero)
            {
                copy = iconHandle;
            }

            try
            {
                using var icon = Icon.FromHandle(copy);
                using var bitmap = CreateTransparentBitmap(copy, icon.Width, icon.Height);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(stream.ToArray());
            }
            catch
            {
                return null;
            }
            finally
            {
                if (copy != IntPtr.Zero && copy != iconHandle)
                {
                    DestroyIcon(copy);
                }
            }
        }

        private static Bitmap CreateTransparentBitmap(IntPtr iconHandle, int width, int height)
        {
            var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);

            var hdc = graphics.GetHdc();
            try
            {
                DrawIconEx(hdc, 0, 0, iconHandle, bitmap.Width, bitmap.Height, 0, IntPtr.Zero, DiNormal);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            return bitmap;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DrawIconEx(
            IntPtr hdc,
            int xLeft,
            int yTop,
            IntPtr hIcon,
            int cxWidth,
            int cyHeight,
            uint istepIfAniCur,
            IntPtr hbrFlickerFreeDraw,
            int diFlags);
    }

    private static class InjecteeLog
    {
        private static readonly object Sync = new();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskBar2",
            "tray-hook-injectee.log");

        [Conditional("DEBUG")]
        public static void Write(string message)
        {
#if !DEBUG
            return;
#else
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath) ?? "");
                    File.AppendAllText(
                        LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{ProcessIdentity.Id}:{ProcessIdentity.Name}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
            }
#endif
        }
    }
}
