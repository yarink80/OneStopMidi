using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneStopMidi.Core.Output;

/// <summary>P/Invoke declarations for the Windows multimedia MIDI output and timer APIs.</summary>
internal static class WinMmNative
{
    private const string Dll = "winmm.dll";

    public const uint MMSYSERR_NOERROR = 0;
    public const uint MIDIERR_STILLPLAYING = 65;
    public const uint CALLBACK_NULL = 0;
    public const int MHDR_DONE = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public uint dwOffset;
        public IntPtr dwReserved0, dwReserved1, dwReserved2, dwReserved3,
                      dwReserved4, dwReserved5, dwReserved6, dwReserved7;
    }

    [DllImport(Dll)] public static extern uint midiOutGetNumDevs();

    [DllImport(Dll, CharSet = CharSet.Unicode, EntryPoint = "midiOutGetDevCapsW")]
    public static extern uint midiOutGetDevCaps(UIntPtr uDeviceID, ref MIDIOUTCAPS caps, uint cbMidiOutCaps);

    [DllImport(Dll)]
    public static extern uint midiOutOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport(Dll)] public static extern uint midiOutShortMsg(IntPtr handle, uint message);
    [DllImport(Dll)] public static extern uint midiOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport(Dll)] public static extern uint midiOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport(Dll)] public static extern uint midiOutLongMsg(IntPtr handle, IntPtr header, uint size);
    [DllImport(Dll)] public static extern uint midiOutReset(IntPtr handle);
    [DllImport(Dll)] public static extern uint midiOutClose(IntPtr handle);

    [DllImport(Dll)] public static extern uint timeBeginPeriod(uint period);
    [DllImport(Dll)] public static extern uint timeEndPeriod(uint period);
}

/// <summary>Enumerates and opens WinMM MIDI output devices. Never throws; failures yield no devices / null ports.</summary>
public sealed class WinMmOutputProvider : IMidiOutputProvider
{
    public IReadOnlyList<MidiDeviceInfo> GetDevices()
    {
        var list = new List<MidiDeviceInfo>();
        try
        {
            uint n = WinMmNative.midiOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var caps = new WinMmNative.MIDIOUTCAPS();
                uint r = WinMmNative.midiOutGetDevCaps((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WinMmNative.MIDIOUTCAPS>());
                list.Add(new MidiDeviceInfo((int)i, r == WinMmNative.MMSYSERR_NOERROR ? caps.szPname : $"MIDI Out {i + 1}"));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Not on Windows / no multimedia support: no devices.
        }
        return list;
    }

    public IMidiOutputPort? Open(int deviceId)
    {
        try
        {
            string name = GetDevices().FirstOrDefault(d => d.Id == deviceId)?.Name ?? $"MIDI Out {deviceId + 1}";
            uint r = WinMmNative.midiOutOpen(out IntPtr handle, unchecked((uint)deviceId), IntPtr.Zero, IntPtr.Zero, WinMmNative.CALLBACK_NULL);
            if (r != WinMmNative.MMSYSERR_NOERROR || handle == IntPtr.Zero) return null;
            return new WinMmOutputPort(handle, name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }
}

/// <summary>An open WinMM MIDI output device.</summary>
public sealed class WinMmOutputPort : IMidiOutputPort
{
    private readonly object _lock = new();
    private IntPtr _handle;

    internal WinMmOutputPort(IntPtr handle, string name)
    {
        _handle = handle;
        Name = name;
    }

    public string Name { get; }

    public void SendShort(byte status, byte data1, byte data2)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            WinMmNative.midiOutShortMsg(_handle, (uint)(status | data1 << 8 | data2 << 16));
        }
    }

    public void SendSysEx(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty) return;
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            int hdrSize = Marshal.SizeOf<WinMmNative.MIDIHDR>();
            IntPtr buffer = Marshal.AllocHGlobal(message.Length);
            IntPtr header = Marshal.AllocHGlobal(hdrSize);
            try
            {
                Marshal.Copy(message.ToArray(), 0, buffer, message.Length);
                var hdr = new WinMmNative.MIDIHDR { lpData = buffer, dwBufferLength = (uint)message.Length, dwBytesRecorded = (uint)message.Length };
                Marshal.StructureToPtr(hdr, header, false);
                if (WinMmNative.midiOutPrepareHeader(_handle, header, (uint)hdrSize) != WinMmNative.MMSYSERR_NOERROR) return;
                if (WinMmNative.midiOutLongMsg(_handle, header, (uint)hdrSize) == WinMmNative.MMSYSERR_NOERROR)
                {
                    int flagsOffset = (int)Marshal.OffsetOf<WinMmNative.MIDIHDR>(nameof(WinMmNative.MIDIHDR.dwFlags));
                    var sw = Stopwatch.StartNew();
                    while ((Marshal.ReadInt32(header, flagsOffset) & WinMmNative.MHDR_DONE) == 0 && sw.ElapsedMilliseconds < 500)
                        Thread.Sleep(0);
                }
                for (int i = 0; i < 50; i++)
                {
                    if (WinMmNative.midiOutUnprepareHeader(_handle, header, (uint)hdrSize) != WinMmNative.MIDIERR_STILLPLAYING) break;
                    Thread.Sleep(1);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero) WinMmNative.midiOutReset(_handle);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            WinMmNative.midiOutReset(_handle);
            WinMmNative.midiOutClose(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
