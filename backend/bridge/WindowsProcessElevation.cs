using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace CodexLanBridge;

/// <summary>
/// Reports the elevation of this exact Bridge process. Group membership is not
/// sufficient here: an administrator running with a filtered UAC token is still
/// unable to perform elevated work. TOKEN_ELEVATION reflects the token that this
/// process and its child processes actually use.
/// </summary>
internal static class WindowsProcessElevation
{
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;

    internal const string BridgeOwnedTasksOnlyScope = "bridgeOwnedTasksOnly";

    internal static AdministratorModeStatus Current { get; } = DetectCurrentProcess();

    private static AdministratorModeStatus DetectCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, false, BridgeOwnedTasksOnlyScope);

        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            return new(false, false, BridgeOwnedTasksOnlyScope);

        try
        {
            var size = Marshal.SizeOf<TokenElevation>();
            if (!GetTokenInformation(
                    token,
                    TokenElevationInformationClass,
                    out var elevation,
                    size,
                    out var returnedLength) ||
                returnedLength < size)
            {
                return new(false, false, BridgeOwnedTasksOnlyScope);
            }

            return new(true, elevation.TokenIsElevated != 0, BridgeOwnedTasksOnlyScope);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed record AdministratorModeStatus(
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("scope")] string Scope);
