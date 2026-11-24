using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MettLauncher.App.Services;

/// <summary>Kurzer, selbst berechneter Ton bei einem neuen Erfolg.</summary>
public static class OverlaySound
{
    private const int Rate = 44100;
    private static readonly Lazy<byte[]> Wav = new(Build);
    private static GCHandle _pinned;

    public static void Play()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!_pinned.IsAllocated) _pinned = GCHandle.Alloc(Wav.Value, GCHandleType.Pinned);
                const uint async = 0x1, noDefault = 0x2, memory = 0x4;
                PlaySound(_pinned.AddrOfPinnedObject(), 0, async | noDefault | memory);
                return;
            }
            var file = Path.Combine(Path.GetTempPath(), "mett-erfolg.wav");
            if (!File.Exists(file) || new FileInfo(file).Length != Wav.Value.Length) File.WriteAllBytes(file, Wav.Value);
            foreach (var player in new[] { "pw-play", "paplay", "aplay" })
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(player, [file])
                    {
                        UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
                    });
                    return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // gibt es hier nicht
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or InvalidOperationException)
        {
            // ohne Ton
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(nint sound, nint module, uint flags);

    /// <summary>Zwei weiche Glockentöne, 16 Bit mono.</summary>
    private static byte[] Build()
    {
        (double Start, double Frequency)[] notes = [(0, 659.26), (0.12, 987.77)];
        var count = (int)(Rate * 0.9);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + count * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(count * 2);
        for (var i = 0; i < count; i++)
        {
            var v = 0.0;
            foreach (var (start, f) in notes)
            {
                var t = i / (double)Rate - start;
                if (t < 0) continue;
                var envelope = Math.Min(t / 0.005, 1) * Math.Exp(-5 * t);
                v += envelope * (Math.Sin(Math.Tau * f * t) + 0.25 * Math.Sin(Math.Tau * 2 * f * t));
            }
            var end = Math.Min(1, (count - i) / (Rate * 0.05));
            w.Write((short)(v * end * 0.16 * short.MaxValue));
        }
        return ms.ToArray();
    }
}
