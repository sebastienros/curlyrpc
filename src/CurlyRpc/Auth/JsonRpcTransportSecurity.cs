using System.Runtime.Versioning;

namespace CurlyRpc;

/// <summary>
/// Transport-level hardening helpers providing defense in depth for connections carried over Unix
/// domain sockets or named pipes. These are optional; the handshake authentication middleware
/// (<see cref="HandshakeAuthenticationMiddleware"/>) remains the primary access control.
/// </summary>
public static class JsonRpcTransportSecurity
{
    /// <summary>
    /// Restricts a Unix domain socket (or any backing file) so that only the current user can read or
    /// write it (mode <c>0600</c>). No-op on Windows, where named pipes and the NTFS default ACL already
    /// scope access to the creating user.
    /// </summary>
    /// <param name="path">The filesystem path of the socket or file to restrict.</param>
    /// <returns><see langword="true"/> if permissions were applied; <see langword="false"/> on Windows.</returns>
    public static bool RestrictToCurrentUser(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        SetOwnerOnlyMode(path);
        return true;
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetOwnerOnlyMode(string path)
        => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
