using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// Wraps an anonymous pipe pair for ConPTY I/O.
/// </summary>
public sealed class PseudoConsolePipe : IDisposable
{
    public SafeFileHandle ReadSide { get; }
    public SafeFileHandle WriteSide { get; }

    public PseudoConsolePipe()
    {
        if (!CreatePipe(out var readSide, out var writeSide, IntPtr.Zero, 0))
            throw new InvalidOperationException(
                $"CreatePipe failed with error {Marshal.GetLastWin32Error()}");

        ReadSide = readSide;
        WriteSide = writeSide;
    }

    public void Dispose()
    {
        ReadSide?.Dispose();
        WriteSide?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        int nSize);
}
