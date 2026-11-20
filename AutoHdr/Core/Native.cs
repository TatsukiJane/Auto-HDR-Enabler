using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutoHdr.Core;

/// <summary>P/Invoke: Display Configuration API (HDR), DDC/CI (monitor picture mode) and process control.</summary>
public static class Native
{
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public enum DeviceInfoType : uint
    {
        GetTargetName = 2,
        GetAdvancedColorInfo = 9,
        SetAdvancedColorState = 10,
        GetAdvancedColorInfo2 = 15, // Windows 11 24H2+
        SetHdrState = 16,           // Windows 11 24H2+
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public readonly long Value => ((long)HighPart << 32) | LowPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathSourceInfo
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathTargetInfo
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public Rational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public LUID AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoHeader
    {
        public DeviceInfoType Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdvancedColorInfo2
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }

    /// <summary>Shared layout for SET_HDR_STATE and SET_ADVANCED_COLOR_STATE: header + 32-bit value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetState
    {
        public DeviceInfoHeader Header;
        public uint Value;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PathInfo[] pathArray,
        ref uint numModeInfoArrayElements, [Out] ModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigSetDeviceInfo(ref SetState request);

    // ---- Monitor settings over DDC/CI ----

    public const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    public const int DISPLAY_DEVICE_ACTIVE = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    public static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    // dxva2.dll is not a KnownDLL: load it only from System32, the tray runs with administrator rights
    [DllImport("dxva2.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool DestroyPhysicalMonitor(IntPtr handle);

    [DllImport("dxva2.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint maximum);

    [DllImport("dxva2.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);

    // ---- Processes ----

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32EnumProcesses([Out] uint[] processIds, uint arraySizeBytes, out uint bytesReturned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr processHandle);

    public const uint TOKEN_QUERY = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    // ---- Files ----

    public const uint FILE_LIST_DIRECTORY = 0x0001;             // without read or write access Windows does not check the share mode
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;  // needed to open a folder
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    // ---- Other ----

    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
