using System.Buffers.Binary;
using System.Diagnostics;

namespace OnimushaDualSense;

/// <summary>Pure construction helpers for the DualSense Bluetooth audio-haptics report.</summary>
static class BluetoothHapticsProtocol
{
    public const int ReportLength = 142;
    public const int PcmLength = 64;

    // The controller needs the audio state packet before it accepts the PCM stream.
    // This is the standard DualSense Bluetooth state used by established haptics clients.
    static readonly byte[] StatePacket =
    [
        0x90, 0x3f,
        0xfd, 0xf7, 0x00, 0x00, 0x7f, 0x7f,
        0xff, 0x09, 0x00, 0x0f, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0a,
        0x07, 0x00, 0x00, 0x02, 0x01, 0x00, 0xff, 0xd7, 0x00
    ];

    public static byte[] StateFrame(int outputLength = ReportLength)
    {
        if (outputLength < ReportLength) throw new ArgumentOutOfRangeException(nameof(outputLength), "Bluetooth haptics output must be at least 142 bytes.");
        var report = new byte[outputLength];
        report[0] = 0x32;
        report[1] = 0x10;
        StatePacket.CopyTo(report, 2);
        WriteCrc(report);
        return report;
    }

    public static byte[] Frame(ReadOnlySpan<byte> stereo8, byte sequence, byte counter, int outputLength = ReportLength)
    {
        if (stereo8.Length != PcmLength) throw new ArgumentException("Bluetooth haptics PCM must contain 64 bytes.", nameof(stereo8));
        if (outputLength < ReportLength) throw new ArgumentOutOfRangeException(nameof(outputLength), "Bluetooth haptics output must be at least 142 bytes.");

        var report = new byte[outputLength];
        report[0] = 0x32;
        report[1] = (byte)((sequence & 0x0f) << 4);

        // Audio packet 0x11: fixed setup/status fields and its packet counter.
        report[2] = 0x91;
        report[3] = 7;
        report[4] = 0xfe;
        report[9] = 0xff;
        report[10] = counter;

        // Audio packet 0x12: 32 stereo frames of signed 8-bit PCM.
        report[11] = 0x92;
        report[12] = 64;
        stereo8.CopyTo(report.AsSpan(13, PcmLength));

        WriteCrc(report);
        return report;
    }

    static void WriteCrc(byte[] report)
    {
        uint crc = ~0xeada2d49u;
        for (int i = 0; i <= 137; i++)
        {
            crc ^= report[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        crc = ~crc;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(138, 4), crc);
    }
}

/// <summary>Produces 10.666 ms Bluetooth haptics packets from the mixer output.</summary>
sealed class BluetoothHaptics : IDisposable
{
    const int SourceFrames = 512;
    const int Channels = 4;
    const int AverageFrames = 16;
    const int PacketFrames = SourceFrames / AverageFrames;
    const float BluetoothPcmScale = 128f;
    const double PacketSeconds = SourceFrames / 48000.0;

    readonly Mixer mixer;
    readonly HidRecovery hid;
    readonly Action<string> log;
    readonly ManualResetEventSlim wake = new(false);
    readonly Thread thread;
    readonly float[] source = new float[SourceFrames * Channels];
    readonly byte[] stereo8 = new byte[BluetoothHapticsProtocol.PcmLength];
    int disposed;

    public BluetoothHaptics(Mixer mixer, HidRecovery hid, Action<string>? log = null)
    {
        this.mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));
        this.hid = hid ?? throw new ArgumentNullException(nameof(hid));
        this.log = log ?? (_ => { });
        thread = new Thread(Run) { IsBackground = true, Name = "DualSense Bluetooth haptics" };
        thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        wake.Set();
        thread.Join();
        wake.Dispose();
    }

    void Run()
    {
        double due = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        while (Volatile.Read(ref disposed) == 0)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double wait = due - now;
            if (wait > 0)
            {
                wake.Wait(TimeSpan.FromSeconds(Math.Min(wait, PacketSeconds)));
                continue;
            }

            try
            {
                mixer.Fill(source, SourceFrames);
                BuildStereo8(source, stereo8);
                hid.TrySendBluetoothHaptics(stereo8, now);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { break; }
            catch (Exception e)
            {
                log($"Bluetooth haptics worker error: {e.Message}");
            }

            // One packet per wake-up and a bounded catch-up window prevent a backlog after a stall.
            due += PacketSeconds;
            if (now - due > PacketSeconds * 2) due = now + PacketSeconds;
        }
    }

    internal static void BuildStereo8(ReadOnlySpan<float> source, Span<byte> destination)
    {
        if (source.Length != SourceFrames * Channels) throw new ArgumentException("Expected 512 interleaved four-channel frames.", nameof(source));
        if (destination.Length != BluetoothHapticsProtocol.PcmLength) throw new ArgumentException("Expected 64-byte stereo PCM output.", nameof(destination));

        for (int packetFrame = 0; packetFrame < PacketFrames; packetFrame++)
        {
            int sourceFrame = packetFrame * AverageFrames;
            float left = 0, right = 0;
            for (int i = 0; i < AverageFrames; i++)
            {
                int offset = (sourceFrame + i) * Channels;
                left += source[offset + 2];
                right += source[offset + 3];
            }
            destination[packetFrame * 2] = unchecked((byte)(sbyte)Math.Clamp(left / AverageFrames * BluetoothPcmScale, -128f, 127f));
            destination[packetFrame * 2 + 1] = unchecked((byte)(sbyte)Math.Clamp(right / AverageFrames * BluetoothPcmScale, -128f, 127f));
        }
    }
}
