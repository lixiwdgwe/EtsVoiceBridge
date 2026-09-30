using System.Runtime.InteropServices;
using System.Text;

namespace EtsVoiceBridge;

internal sealed record AudioDevice(uint Id, string Name)
{
    public override string ToString() => Name;
}

internal static class WaveOutput
{
    private const uint Done = 0x00000001;
    private const uint WaveMapper = uint.MaxValue;

    public static IReadOnlyList<AudioDevice> Devices()
    {
        var devices = new List<AudioDevice> { new(WaveMapper, "系统默认播放设备") };
        for (uint index = 0; index < waveOutGetNumDevs(); index++)
        {
            if (waveOutGetDevCapsW(index, out var caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0)
                devices.Add(new AudioDevice(index, caps.Name));
        }
        return devices;
    }

    public static Task PlayAsync(byte[] wav, uint deviceId, CancellationToken token) =>
        Task.Run(() => Play(wav, deviceId, token), token);

    private static void Play(byte[] wav, uint deviceId, CancellationToken token)
    {
        var (format, audio) = ParseWav(wav);
        var result = waveOutOpen(out var handle, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) throw new InvalidOperationException($"无法打开播放设备，WinMM 错误 {result}。");

        IntPtr dataPtr = IntPtr.Zero;
        IntPtr headerPtr = IntPtr.Zero;
        var prepared = false;
        try
        {
            dataPtr = Marshal.AllocHGlobal(audio.Length);
            Marshal.Copy(audio, 0, dataPtr, audio.Length);
            var header = new WaveHeader { Data = dataPtr, BufferLength = (uint)audio.Length };
            var headerSize = (uint)Marshal.SizeOf<WaveHeader>();
            headerPtr = Marshal.AllocHGlobal((int)headerSize);
            Marshal.StructureToPtr(header, headerPtr, false);
            result = waveOutPrepareHeader(handle, headerPtr, headerSize);
            if (result != 0) throw new InvalidOperationException($"无法准备音频，WinMM 错误 {result}。");
            prepared = true;
            result = waveOutWrite(handle, headerPtr, headerSize);
            if (result != 0) throw new InvalidOperationException($"无法播放音频，WinMM 错误 {result}。");
            while ((Marshal.PtrToStructure<WaveHeader>(headerPtr).Flags & Done) == 0)
            {
                if (token.IsCancellationRequested)
                {
                    waveOutReset(handle);
                    token.ThrowIfCancellationRequested();
                }
                Thread.Sleep(25);
            }
        }
        finally
        {
            if (prepared)
            {
                waveOutReset(handle);
                waveOutUnprepareHeader(handle, headerPtr, (uint)Marshal.SizeOf<WaveHeader>());
            }
            waveOutClose(handle);
            if (headerPtr != IntPtr.Zero) Marshal.FreeHGlobal(headerPtr);
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
        }
    }

    internal static (WaveFormat Format, byte[] Audio) ParseWav(byte[] wav)
    {
        using var stream = new MemoryStream(wav);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("不是 RIFF 文件。");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("不是 WAVE 文件。");
        WaveFormat? format = null;
        byte[]? audio = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var start = stream.Position;
            if (size > stream.Length - start) throw new InvalidDataException("WAV 数据长度无效。");
            if (chunk == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAV 格式块过短。");
                var encoding = reader.ReadUInt16();
                var channels = reader.ReadUInt16();
                var sampleRate = reader.ReadUInt32();
                var byteRate = reader.ReadUInt32();
                var blockAlign = reader.ReadUInt16();
                var bits = reader.ReadUInt16();
                if (encoding != 1 || bits != 16 || channels is < 1 or > 2)
                    throw new NotSupportedException("仅支持 16 位 PCM 单声道或双声道 WAV。");
                format = new WaveFormat
                {
                    FormatTag = encoding, Channels = channels, SamplesPerSec = sampleRate,
                    AvgBytesPerSec = byteRate, BlockAlign = blockAlign, BitsPerSample = bits
                };
            }
            else if (chunk == "data") audio = reader.ReadBytes((int)size);
            stream.Position = start + size + (size % 2);
        }
        if (format is null || audio is null || audio.Length == 0)
            throw new InvalidDataException("WAV 缺少格式或音频数据。");
        return (format.Value, audio);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCapsW(uint id, out WaveOutCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint id,
        ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
