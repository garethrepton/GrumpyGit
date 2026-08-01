using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// Manages a Windows Pseudo Console (ConPTY).
/// Minimum Windows 10 version 1809 (build 17763).
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public IntPtr Handle => _handle;

    private PseudoConsole(IntPtr handle)
    {
        _handle = handle;
    }

    public static PseudoConsole Create(
        SafeFileHandle inputReadSide,
        SafeFileHandle outputWriteSide,
        short width,
        short height)
    {
        var size = new COORD { X = width, Y = height };
        int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var handle);
        if (hr != 0)
            throw new InvalidOperationException(
                $"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        return new PseudoConsole(handle);
    }

    public void Resize(short width, short height)
    {
        if (_disposed) return;
        var size = new COORD { X = width, Y = height };
        ResizePseudoConsole(_handle, size);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClosePseudoConsole(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);
}
