using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OnimushaDualSense;

sealed class HidUnavailableException(string message) : Exception(message);

sealed class AudioDeviceUnavailableException : InvalidOperationException
{
    public AudioDeviceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

sealed class AudioCallbackException : Exception
{
    public AudioCallbackException(Exception inner) : base("Audio callback failed", inner) { }
}

interface IHidOutput : IDisposable
{
    void Send(byte[] report);
}

interface IBluetoothHapticsOutput
{
    bool SupportsBluetoothHaptics { get; }
    void SendBluetoothHapticsState();
    void SendBluetoothHaptics(byte[] stereo8);
}

enum HidTransport { Usb, Bluetooth }

sealed class Hid : IHidOutput, IBluetoothHapticsOutput
{
    const uint FileFlagOverlapped = 0x40000000;
    const uint ErrorIoPending = 997;
    const uint ErrorOperationAborted = 995;
    const uint ErrorNotFound = 1168;
    const uint ErrorTimeout = 1460;
    const uint WaitObject0 = 0;
    const uint WaitTimeout = 258;
    const int BluetoothWriteTimeoutMilliseconds = 250;
    [StructLayout(LayoutKind.Sequential)] struct Overlapped
    {
        public nint Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public nint Event;
    }
    readonly SafeFileHandle handle;
    readonly int reportLength;
    readonly HidTransport transport;
    byte sequence;
    byte hapticsSequence;
    byte hapticsCounter;
    public record Device(string Path, ushort Product, int ReportLength)
    {
        public string Model => Product == 0x0df2 ? "DualSense Edge" : "DualSense";
        public HidTransport Transport => Hid.ClassifyPath(Path)!.Value;
    }
    public static HidTransport? ClassifyPath(string path) =>
        IsBluetoothPath(path) ? HidTransport.Bluetooth :
        path.Contains("vid_054c&pid_", StringComparison.OrdinalIgnoreCase) ? HidTransport.Usb : null;
    public static bool IsBluetoothPath(string path) =>
        path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
        || path.Contains("HID#{00001124-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase)
        || path.Contains("VID&0002054C_PID&", StringComparison.OrdinalIgnoreCase);
    public static bool Supported(string path, ushort vendor, ushort product, int usage, int page, int length) =>
        Supported(ClassifyPath(path), vendor, product, usage, page, length);
    public static bool Supported(HidTransport? transport, ushort vendor, ushort product, int usage, int page, int length) =>
        transport is not null && vendor == 0x054c && product is 0x0ce6 or 0x0df2 && usage == 5 && page == 1 &&
        (transport == HidTransport.Bluetooth ? length >= 78 : length is >= 48 and <= 64);
    public static bool Supported(ushort vendor, ushort product, int usage, int page, int length) =>
        Supported(HidTransport.Usb, vendor, product, usage, page, length);
    public static byte[] PadReport(byte[] report, int length)
    {
        if (report.Length != 48 || length is < 48 or > 64) throw new InvalidDataException("Unsupported USB report length");
        if (length == report.Length) return report;
        var padded = new byte[length]; report.CopyTo(padded, 0); return padded;
    }
    [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public uint Size; public Guid ClassGuid; public uint Flags; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort Vendor, Product, Version; }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(nint data, nint caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint SetupDiGetClassDevsW(ref Guid guid, nint enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(nint info, nint device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetailW(nint info, ref InterfaceData data, nint detail, uint size, out uint required, nint device);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(nint info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle handle, byte[] data, uint size, out uint written, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle handle, nint data, uint size, out uint written, nint overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateEventW(nint attributes, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle handle, nint overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle handle, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
    public static List<Device> Find()
    {
        HidD_GetHidGuid(out var guid); nint info = SetupDiGetClassDevsW(ref guid, 0, 0, 0x12);
        if (info == -1) throw new Win32Exception();
        var found = new List<Device>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(info, 0, ref guid, i, ref data))
                { if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(); break; }
                SetupDiGetDeviceInterfaceDetailW(info, ref data, 0, 0, out uint required, 0);
                nint detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, 8); // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize on Windows x64.
                    if (!SetupDiGetDeviceInterfaceDetailW(info, ref data, detail, required, out _, 0)) throw new Win32Exception();
                    string path = Marshal.PtrToStringUni(detail + 4)!;
                    var transport = ClassifyPath(path); if (transport == null) continue;
                    using var probe = CreateFileW(path, 0, 3, 0, 3, 0, 0);
                    var attr = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (probe.IsInvalid || !HidD_GetAttributes(probe, ref attr) || attr.Vendor != 0x054c || attr.Product is not (0x0ce6 or 0x0df2)) continue;
                    if (!HidD_GetPreparsedData(probe, out var preparsed)) continue;
                    nint caps = Marshal.AllocHGlobal(64);
                    try
                    {
                        if (HidP_GetCaps(preparsed, caps) == 0x110000 && Supported(transport, attr.Vendor, attr.Product,
                            Marshal.ReadInt16(caps, 0), Marshal.ReadInt16(caps, 2), Marshal.ReadInt16(caps, 6)))
                            found.Add(new(path, attr.Product, Marshal.ReadInt16(caps, 6)));
                    }
                    finally { Marshal.FreeHGlobal(caps); HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(info); }
        return found;
    }
    public Hid()
    {
        var paths = Find(); if (paths.Count != 1) throw new HidUnavailableException($"Expected one DualSense HID; found {paths.Count}");
        reportLength = paths[0].ReportLength;
        transport = paths[0].Transport;
        handle = CreateFileW(paths[0].Path, 0x40000000, 3, 0, 3, transport == HidTransport.Bluetooth ? FileFlagOverlapped : 0, 0);
        if (handle.IsInvalid) throw new Win32Exception();
        string transportName = transport == HidTransport.Bluetooth ? "Bluetooth" : "USB";
        Files.Log($"{transportName} controller: {paths[0].Model}; PID={paths[0].Product:x4}; output report={reportLength} bytes.");
    }
    public void Send(byte[] report)
    {
        report = transport == HidTransport.Bluetooth ? FrameBluetooth(report, reportLength, ref sequence) : PadReport(report, reportLength);
        WriteReport(report);
    }
    public bool SupportsBluetoothHaptics => transport == HidTransport.Bluetooth && reportLength >= BluetoothHapticsProtocol.ReportLength;
    public void SendBluetoothHapticsState()
    {
        if (!SupportsBluetoothHaptics) throw new InvalidDataException("Bluetooth haptics require a 142-byte output report collection");
        WriteReport(BluetoothHapticsProtocol.StateFrame(reportLength));
    }
    public void SendBluetoothHaptics(byte[] stereo8)
    {
        if (!SupportsBluetoothHaptics) throw new InvalidDataException("Bluetooth haptics require a 142-byte output report collection");
        WriteReport(BluetoothHapticsProtocol.Frame(stereo8, hapticsSequence, hapticsCounter, reportLength));
        hapticsSequence = (byte)((hapticsSequence + 1) & 0x0f);
        hapticsCounter++;
    }
    void WriteReport(byte[] report)
    {
        if (transport == HidTransport.Bluetooth)
        {
            WriteBluetoothReport(report);
            return;
        }
        if (!WriteFile(handle, report, (uint)report.Length, out uint count, 0) || count != report.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DualSense HID write failed");
    }
    void WriteBluetoothReport(byte[] report)
    {
        nint eventHandle = CreateEventW(0, true, false, null);
        if (eventHandle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create Bluetooth HID write event");
        nint overlapped = 0;
        GCHandle pinned = default;
        bool submitted = false, completed = false;
        try
        {
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
            Marshal.StructureToPtr(new Overlapped { Event = eventHandle }, overlapped, false);
            pinned = GCHandle.Alloc(report, GCHandleType.Pinned);
            bool immediate = WriteFile(handle, pinned.AddrOfPinnedObject(), (uint)report.Length, out uint count, overlapped);
            if (!immediate)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending) throw new Win32Exception(error, "Bluetooth HID write failed");
            }
            submitted = true;
            uint wait = WaitForSingleObject(eventHandle, BluetoothWriteTimeoutMilliseconds);
            if (wait == WaitTimeout)
            {
                if (!CancelIoEx(handle, overlapped))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorNotFound) throw new Win32Exception(error, "Bluetooth HID write cancellation failed");
                }
                // CancelIoEx completes the pending HID write before this wait returns.
                if (!GetOverlappedResult(handle, overlapped, out _, true))
                {
                    int error = Marshal.GetLastWin32Error();
                    completed = true;
                    throw new Win32Exception(error == 0 ? (int)ErrorOperationAborted : error, "Bluetooth HID write timed out and was canceled");
                }
                completed = true;
                throw new Win32Exception((int)ErrorTimeout, "Bluetooth HID write timed out");
            }
            if (wait != WaitObject0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Bluetooth HID write wait failed");
            completed = true;
            if (!GetOverlappedResult(handle, overlapped, out count, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Bluetooth HID write failed");
            if (count != report.Length) throw new Win32Exception(24, "Bluetooth HID write was incomplete");
        }
        finally
        {
            if (submitted && !completed)
            {
                CancelIoEx(handle, overlapped);
                GetOverlappedResult(handle, overlapped, out _, true);
            }
            if (pinned.IsAllocated) pinned.Free();
            if (overlapped != 0) Marshal.FreeHGlobal(overlapped);
            CloseHandle(eventHandle);
        }
    }
    public static byte[] FrameBluetooth(byte[] report, int length, ref byte sequence)
    {
        if (report.Length != 48 || length < 78) throw new InvalidDataException("Unsupported Bluetooth report length");
        var framed = new byte[length]; framed[0] = 0x31; framed[1] = (byte)(sequence << 4); framed[2] = 0x10;
        Array.Copy(report, 1, framed, 3, 47);
        uint crc = 0xffffffff;
        static uint Step(uint value, byte b)
        {
            value ^= b;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320 : value >> 1;
            return value;
        }
        crc = Step(crc, 0xa2);
        for (int i = 0; i < 74; i++) crc = Step(crc, framed[i]);
        crc = ~crc;
        framed[74] = (byte)crc; framed[75] = (byte)(crc >> 8); framed[76] = (byte)(crc >> 16); framed[77] = (byte)(crc >> 24);
        sequence = (byte)((sequence + 1) & 0x0f);
        return framed;
    }
    public void Dispose() => handle.Dispose();
}

sealed class HidRecovery : IDisposable
{
    readonly Func<IHidOutput> factory;
    readonly Action<string> log;
    readonly object gate = new();
    IHidOutput? output;
    double nextOpen;
    double backoff = .5;
    bool bluetoothHapticsInitialized;
    bool disposed;

    public HidRecovery(Func<IHidOutput> factory, Action<string> log)
        : this(null, factory, log) { }

    public HidRecovery(IHidOutput? initial, Func<IHidOutput> factory, Action<string> log)
    {
        output = initial;
        this.factory = factory;
        this.log = log;
    }

    public bool TrySend(byte[] report, double now)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(HidRecovery));
            if (!EnsureOpen(now)) return false;
            try
            {
                output!.Send(report);
                backoff = .5;
                return true;
            }
            catch (Win32Exception e)
            {
                log($"DualSense HID write failed; native error {e.NativeErrorCode}: {e.Message}; retrying in {backoff:0.0}s.");
                ReleaseFailedOutput();
                ScheduleRetry(now);
                return false;
            }
        }
    }

    public bool TrySendBluetoothHaptics(byte[] stereo8, double now)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(HidRecovery));
            if (!EnsureOpen(now)) return false;
            if (output is not IBluetoothHapticsOutput haptics || !haptics.SupportsBluetoothHaptics) return false;
            try
            {
                if (!bluetoothHapticsInitialized)
                {
                    haptics.SendBluetoothHapticsState();
                    bluetoothHapticsInitialized = true;
                    log("Bluetooth HID haptics initialized; direct PCM stream active.");
                }
                haptics.SendBluetoothHaptics(stereo8);
                backoff = .5;
                return true;
            }
            catch (Win32Exception e)
            {
                log($"Bluetooth HID haptics write failed; native error {e.NativeErrorCode}: {e.Message}; retrying in {backoff:0.0}s.");
                ReleaseFailedOutput();
                ScheduleRetry(now);
                return false;
            }
        }
    }

    // Drops the current handle without opening a replacement. The next normal
    // send will use the factory and therefore observe the current transport.
    public void Rebind()
    {
        lock (gate)
        {
            if (disposed) return;
            ReleaseCurrentOutput();
            nextOpen = 0;
            backoff = .5;
        }
    }

    bool EnsureOpen(double now)
    {
        if (output != null) return true;
        if (now < nextOpen) return false;
        IHidOutput candidate;
        try { candidate = factory(); }
        catch (Win32Exception e) { ReopenFailed(now, e); return false; }
        catch (HidUnavailableException e) { ReopenFailed(now, e); return false; }
        try
        {
            candidate.Send(Protocol.Report(audio: false));
            output = candidate;
            bluetoothHapticsInitialized = false;
            backoff = .5;
            log("DualSense HID output opened; both triggers released.");
            return true;
        }
        catch (Win32Exception e)
        {
            DisposeFailed(candidate);
            ReopenFailed(now, e);
            return false;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            if (output == null) return;
            try { output.Send(Protocol.Report(audio: false)); }
            catch (Win32Exception e) { log($"DualSense HID release failed; native error {e.NativeErrorCode}: {e.Message}"); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ReleaseCurrentOutput();
            bluetoothHapticsInitialized = false;
        }
    }

    void ReleaseCurrentOutput()
    {
        if (output == null) return;
        var current = output;
        output = null;
        bluetoothHapticsInitialized = false;
        try { current.Send(Protocol.Report(audio: false)); }
        catch (Win32Exception e) { log($"DualSense HID release failed; native error {e.NativeErrorCode}: {e.Message}"); }
        finally { DisposeFailed(current); }
    }

    void ReleaseFailedOutput()
    {
        var failed = output;
        output = null;
        bluetoothHapticsInitialized = false;
        if (failed == null) return;
        try { failed.Send(Protocol.Report(audio: false)); }
        catch (Win32Exception e) { log($"DualSense HID release after failure failed; native error {e.NativeErrorCode}: {e.Message}"); }
        finally { failed.Dispose(); }
    }

    void DisposeFailed(IHidOutput failed)
    {
        try { failed.Dispose(); }
        catch (Win32Exception e) { log($"DualSense HID failed-output dispose failed; native error {e.NativeErrorCode}: {e.Message}"); }
    }

    void ReopenFailed(double now, Exception e)
    {
        log($"DualSense HID reopen failed: {e.Message}; retrying in {backoff:0.0}s.");
        ScheduleRetry(now);
    }

    void ScheduleRetry(double now)
    {
        nextOpen = now + backoff;
        backoff = Math.Min(5, backoff * 2);
    }
}

static class Focus
{
    static uint cachedPid;
    static double expires;
    static bool cachedResult;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    public static bool IsGame()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            double now = Files.Now;
            if (pid == cachedPid && now < expires) return cachedResult;
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            cachedResult = process.ProcessName.Equals("OnimushaWotS", StringComparison.OrdinalIgnoreCase);
            cachedPid = pid; expires = now + 1; return cachedResult;
        }
        catch { return false; }
    }
}

sealed class Audio : IDisposable
{
    const string Dll = "libportaudio64bit.dll";
    [StructLayout(LayoutKind.Sequential)] struct DeviceInfo
    { public int Version; public nint Name; public int HostApi, Inputs, Outputs; public double LowInput, LowOutput, HighInput, HighOutput, Rate; }
    [StructLayout(LayoutKind.Sequential)] struct HostInfo { public int Version, Type; public nint Name; public int Count, Input, Output; }
    [StructLayout(LayoutKind.Sequential)] struct Parameters { public int Device, Channels; public uint Format; public double Latency; public nint Specific; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Callback(nint input, nint output, uint frames, nint timing, uint flags, nint user);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_Initialize();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_Terminate();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_GetDeviceCount();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint Pa_GetDeviceInfo(int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint Pa_GetHostApiInfo(int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint Pa_GetErrorText(int code);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_OpenStream(out nint stream, nint input, ref Parameters output, double rate, uint frames, uint flags, Callback callback, nint data);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_StartStream(nint stream);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_AbortStream(nint stream);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_CloseStream(nint stream);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int Pa_IsStreamActive(nint stream);
    readonly Callback callback;
    readonly float[] buffer = new float[256 * 4];
    nint stream;
    bool initialized;
    int underflows;
    Exception? error;
    public int Underflows => Volatile.Read(ref underflows);
    static void Check(int code) { if (code < 0) throw new InvalidOperationException($"PortAudio {code}: {Marshal.PtrToStringUTF8(Pa_GetErrorText(code))}"); }
    public double OutputLatency { get; private set; }
    public static bool IsDualSenseOutputName(string name) =>
        name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Wireless Controller", StringComparison.OrdinalIgnoreCase);
    public static void Diagnose()
    {
        bool initialized = false;
        try
        {
            Check(Pa_Initialize()); initialized = true;
            int count = Pa_GetDeviceCount(); Check(count);
            Console.WriteLine($"PortAudio devices: {count}");
            for (int i = 0; i < count; i++)
            {
                var d = Marshal.PtrToStructure<DeviceInfo>(Pa_GetDeviceInfo(i));
                var host = Marshal.PtrToStructure<HostInfo>(Pa_GetHostApiInfo(d.HostApi));
                string name = Marshal.PtrToStringUTF8(d.Name) ?? "";
                string hostName = Marshal.PtrToStringUTF8(host.Name) ?? "";
                Console.WriteLine($"Audio device {i}: host={hostName} type={host.Type} inputs={d.Inputs} outputs={d.Outputs} dualsense={IsDualSenseOutputName(name)} name={name}");
            }
        }
        finally { if (initialized) Pa_Terminate(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct StreamInfo { public int Version; public double InputLatency, OutputLatency, Rate; }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern nint Pa_GetStreamInfo(nint stream);
    public Audio(Mixer mixer, bool normalOutput = false)
    {
        float[] stereo = new float[256 * 2];
        callback = (input, output, frames, timing, flags, user) =>
        {
            try
            {
                if (frames != 256) throw new InvalidOperationException("Unexpected audio buffer size");
                if (flags != 0) Interlocked.Increment(ref underflows);
                mixer.Fill(buffer, (int)frames);
                if (normalOutput) { for (int i = 0; i < 256; i++) { stereo[i * 2] = buffer[i * 4 + 2]; stereo[i * 2 + 1] = buffer[i * 4 + 3]; } Marshal.Copy(stereo, 0, output, stereo.Length); }
                else Marshal.Copy(buffer, 0, output, buffer.Length);
                return 0;
            }
            catch (Exception e) { error = e; return 2; }
        };
        try
        {
            Check(Pa_Initialize()); initialized = true;
            int count = Pa_GetDeviceCount(); Check(count);
            var found = new List<(int Index, DeviceInfo Info)>();
            for (int i = 0; i < count; i++)
            {
                var d = Marshal.PtrToStructure<DeviceInfo>(Pa_GetDeviceInfo(i));
                var host = Marshal.PtrToStructure<HostInfo>(Pa_GetHostApiInfo(d.HostApi));
                if (normalOutput ? host.Type == 13 && host.Output == i && d.Outputs >= 2 : d.Outputs == 4 && host.Type == 13 && IsDualSenseOutputName(Marshal.PtrToStringUTF8(d.Name) ?? "")) found.Add((i, d));
            }
            if (found.Count != 1) throw new InvalidOperationException($"Expected one four-channel DualSense WASAPI device; found {found.Count}");
            var parameters = new Parameters { Device = found[0].Index, Channels = normalOutput ? 2 : 4, Format = 1, Latency = found[0].Info.LowOutput };
            Check(Pa_OpenStream(out stream, 0, ref parameters, 48000, 256, 0, callback, 0)); Check(Pa_StartStream(stream));
            OutputLatency = Marshal.PtrToStructure<StreamInfo>(Pa_GetStreamInfo(stream)).OutputLatency;
        }
        catch { Dispose(); throw; }
    }
    public void CheckHealth()
    {
        if (error != null) throw new AudioCallbackException(error);
        int state = Pa_IsStreamActive(stream);
        if (state < 0)
        {
            try { Check(state); }
            catch (InvalidOperationException e) { throw new AudioDeviceUnavailableException(e.Message, e); }
        }
        if (state == 0) throw new AudioDeviceUnavailableException("Audio device disconnected or stopped");
    }
    public void Dispose()
    {
        if (stream != 0) { Pa_AbortStream(stream); Pa_CloseStream(stream); stream = 0; }
        if (initialized) { Pa_Terminate(); initialized = false; }
        GC.KeepAlive(callback);
    }
}
