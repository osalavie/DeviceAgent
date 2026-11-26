using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;

namespace Sim.DeviceAgent.App.Devices;

internal sealed record SerialPortInfo(string PortName, string DisplayName);

/// <summary>枚举本机 COM 口及对应的 Windows 设备名称（如 “COM5 · Serial Port Simulator (UMDF 2)”）。</summary>
internal static class SerialPortCatalog
{
    private static readonly Guid PortsClassGuid = new("4d36e978-e325-11ce-bfc1-08002be10318");

    private const uint DigcfPresent = 0x02;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const uint KeyRead = 0x00020019;

    public static IReadOnlyList<SerialPortInfo> Enumerate()
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var classGuid = PortsClassGuid;
        var deviceInfoSet = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DigcfPresent);
        if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != new IntPtr(-1))
        {
            try
            {
                var deviceInfo = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                for (uint index = 0; SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo); index++)
                {
                    var friendlyName = ReadFriendlyName(deviceInfoSet, ref deviceInfo);
                    var portName = ReadPortName(deviceInfoSet, ref deviceInfo);
                    if (string.IsNullOrWhiteSpace(portName))
                    {
                        portName = ExtractPortName(friendlyName);
                    }

                    if (string.IsNullOrWhiteSpace(portName))
                    {
                        continue;
                    }

                    results[portName] = BuildDisplayName(portName, friendlyName);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        foreach (var portName in SerialPort.GetPortNames())
        {
            if (!results.ContainsKey(portName))
            {
                results[portName] = portName;
            }
        }

        return results
            .Select(pair => new SerialPortInfo(pair.Key, pair.Value))
            .OrderBy(info => PortNumber(info.PortName))
            .ThenBy(info => info.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildDisplayName(string portName, string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return portName;
        }

        var name = friendlyName.Trim();
        var suffix = $"({portName})";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^suffix.Length].TrimEnd();
        }

        return name.Length == 0 ? portName : $"{portName} · {name}";
    }

    private static string? ReadFriendlyName(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfo)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfo,
                SpdrpFriendlyName,
                out _,
                buffer,
                (uint)buffer.Length,
                out var required))
        {
            return null;
        }

        var length = (int)Math.Min(required, (uint)buffer.Length);
        return Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0').Trim();
    }

    private static string? ReadPortName(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfo)
    {
        var key = SetupDiOpenDevRegKey(deviceInfoSet, ref deviceInfo, DicsFlagGlobal, 0, DiregDev, KeyRead);
        if (key == IntPtr.Zero || key == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var buffer = new byte[512];
            var size = (uint)buffer.Length;
            var result = RegQueryValueExW(key, "PortName", IntPtr.Zero, out _, buffer, ref size);
            return result == 0
                ? Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0').Trim()
                : null;
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static string? ExtractPortName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return null;
        }

        var open = friendlyName.LastIndexOf('(');
        var close = friendlyName.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var candidate = friendlyName[(open + 1)..close].Trim();
            if (candidate.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static int PortNumber(string name)
        => name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
           int.TryParse(name.AsSpan(3), out var number)
            ? number
            : int.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint scope,
        uint hwProfile,
        uint keyType,
        uint samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(
        IntPtr hKey,
        string valueName,
        IntPtr reserved,
        out uint type,
        byte[] data,
        ref uint dataSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
