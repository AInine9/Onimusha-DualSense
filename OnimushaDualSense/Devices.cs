using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OnimushaDualSense;

sealed class HidUnavailableException(string message) : Exception(message);

interface IHidOutput : IDisposable
{
    void Send(byte[] report);
}

sealed class Hid : IHidOutput
{
    readonly SafeFileHandle handle;
    readonly int reportLength;
    public record Device(string Path, ushort Product, int ReportLength)
    {
        public string Model => Product == 0x0df2 ? "DualSense Edge" : "DualSense";
    }
    public static bool Supported(ushort vendor, ushort product, int usage, int page, int length) =>
        vendor == 0x054c && product is 0x0ce6 or 0x0df2 && usage == 5 && page == 1 && length is >= 48 and <= 64;
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
                    // USB devices expose VID/PID here; Bluetooth uses a BTHENUM path.
                    if (!path.Contains("vid_054c&pid_", StringComparison.OrdinalIgnoreCase)) continue;
                    using var probe = CreateFileW(path, 0, 3, 0, 3, 0, 0);
                    var attr = new Attributes { Size = Marshal.SizeOf<Attributes>() };
                    if (probe.IsInvalid || !HidD_GetAttributes(probe, ref attr) || attr.Vendor != 0x054c || attr.Product is not (0x0ce6 or 0x0df2)) continue;
                    if (!HidD_GetPreparsedData(probe, out var preparsed)) continue;
                    nint caps = Marshal.AllocHGlobal(64);
                    try
                    {
                        if (HidP_GetCaps(preparsed, caps) == 0x110000 && Supported(attr.Vendor, attr.Product,
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
        var paths = Find(); if (paths.Count != 1) throw new HidUnavailableException($"Expected one USB DualSense HID; found {paths.Count}");
        reportLength = paths[0].ReportLength;
        handle = CreateFileW(paths[0].Path, 0x40000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid) throw new Win32Exception();
        Files.Log($"USB controller: {paths[0].Model}; PID={paths[0].Product:x4}; output report={reportLength} bytes.");
    }
    public void Send(byte[] report)
    {
        report = PadReport(report, reportLength);
        if (!WriteFile(handle, report, (uint)report.Length, out uint count, 0) || count != report.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "USB HID write failed");
    }
    public void Dispose() => handle.Dispose();
}

sealed class HidRecovery : IDisposable
{
    readonly Func<IHidOutput> factory;
    readonly Action<string> log;
    IHidOutput? output;
    double nextOpen;
    double backoff = .5;
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
        if (disposed) throw new ObjectDisposedException(nameof(HidRecovery));
        if (output == null)
        {
            if (now < nextOpen) return false;
            IHidOutput candidate;
            try { candidate = factory(); }
            catch (Win32Exception e) { ReopenFailed(now, e); return false; }
            catch (HidUnavailableException e) { ReopenFailed(now, e); return false; }
            try
            {
                candidate.Send(Protocol.Report(audio: false));
                output = candidate;
                backoff = .5;
                log("USB HID output opened; both triggers released.");
            }
            catch (Win32Exception e)
            {
                DisposeFailed(candidate);
                ReopenFailed(now, e);
                return false;
            }
        }
        try
        {
            output.Send(report);
            backoff = .5;
            return true;
        }
        catch (Win32Exception e)
        {
            log($"USB HID write failed; native error {e.NativeErrorCode}: {e.Message}; retrying in {backoff:0.0}s.");
            ReleaseFailedOutput();
            ScheduleRetry(now);
            return false;
        }
    }

    public void Release()
    {
        if (output == null) return;
        try { output.Send(Protocol.Report(audio: false)); }
        catch (Win32Exception e) { log($"USB HID release failed; native error {e.NativeErrorCode}: {e.Message}"); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Release();
        output?.Dispose();
        output = null;
    }

    void ReleaseFailedOutput()
    {
        var failed = output;
        output = null;
        if (failed == null) return;
        try { failed.Send(Protocol.Report(audio: false)); }
        catch (Win32Exception e) { log($"USB HID release after failure failed; native error {e.NativeErrorCode}: {e.Message}"); }
        finally { failed.Dispose(); }
    }

    void DisposeFailed(IHidOutput failed)
    {
        try { failed.Dispose(); }
        catch (Win32Exception e) { log($"USB HID failed-output dispose failed; native error {e.NativeErrorCode}: {e.Message}"); }
    }

    void ReopenFailed(double now, Exception e)
    {
        log($"USB HID reopen failed: {e.Message}; retrying in {backoff:0.0}s.");
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
        if (error != null) throw new InvalidOperationException("Audio callback failed", error);
        int state = Pa_IsStreamActive(stream); Check(state);
        if (state == 0) throw new InvalidOperationException("Audio device disconnected or stopped");
    }
    public void Dispose()
    {
        if (stream != 0) { Pa_AbortStream(stream); Pa_CloseStream(stream); stream = 0; }
        if (initialized) { Pa_Terminate(); initialized = false; }
        GC.KeepAlive(callback);
    }
}
