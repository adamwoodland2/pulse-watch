using System.IO;
using System.Media;

namespace ConnectionChecker.Services;

/// <summary>
/// Synthesizes two short, quiet two-note pings (rising for recovery, falling
/// for down) as in-memory WAVs at startup, so no audio assets are shipped.
/// </summary>
public static class SoundService
{
    private const int SampleRate = 44100;
    private const double Amplitude = 0.18; // subtle — well below full scale
    private const double NoteSeconds = 0.13;

    private static readonly byte[] DownWav = BuildTwoNoteWav(523.25, 349.23);  // C5 -> F4 falling
    private static readonly byte[] UpWav = BuildTwoNoteWav(523.25, 783.99);    // C5 -> G5 rising

    public static void PlayDown() => Play(DownWav);
    public static void PlayUp() => Play(UpWav);

    private static void Play(byte[] wav)
    {
        try
        {
            // SoundPlayer.Play is async (worker thread); a new instance per call
            // lets overlapping alerts each ping without cutting the previous off badly.
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch
        {
            // No audio device / audio service down — alerts still show visually.
        }
    }

    private static byte[] BuildTwoNoteWav(double freq1, double freq2)
    {
        var samples = GenerateNote(freq1).Concat(GenerateNote(freq2)).ToArray();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataLen);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);      // byte rate
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits per sample
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }

    private static short[] GenerateNote(double freq)
    {
        int count = (int)(SampleRate * NoteSeconds);
        var samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / SampleRate;
            // Quick attack, exponential decay — reads as a soft "blip", not a buzzer.
            double envelope = Math.Min(1.0, i / (SampleRate * 0.005)) * Math.Exp(-6.0 * t / NoteSeconds);
            samples[i] = (short)(Math.Sin(2 * Math.PI * freq * t) * envelope * Amplitude * short.MaxValue);
        }
        return samples;
    }
}
