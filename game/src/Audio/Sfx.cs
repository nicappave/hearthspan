using System;
using System.Collections.Generic;
using Godot;

namespace Hearthspan.Game.Audio;

public enum Sound { Click, Select, Order, EndTurn, Bell, Fanfare, War, Peace, Error }

/// <summary>
/// Plan v5: the game's sounds, synthesized in code at startup — no audio assets (downloading any would
/// need Nicolas's approval, and a handful of soft, short tones is all a turn-based map needs for
/// feedback). A soft tick for buttons, a two-tone blip for selecting, a thud for a march order, a
/// warm chord for a new turn, bells and a small fanfare for the moment cards, a drum for war. Each is a
/// 16-bit mono <see cref="AudioStreamWav"/> built once; a small pool of players lets them overlap.
/// Everything goes through the Master bus, so the settings volume slider governs it.
/// </summary>
public partial class Sfx : Node
{
    private const int Rate = 22050;
    private static Sfx? _instance;
    private readonly Dictionary<Sound, AudioStreamWav> _streams = new();
    private readonly List<AudioStreamPlayer> _players = new();
    private int _next;
    private readonly Dictionary<Sound, ulong> _lastPlayed = new();

    public override void _Ready()
    {
        _instance = this;
        foreach (Sound s in Enum.GetValues<Sound>()) _streams[s] = ToWav(Synthesize(s));
        for (int i = 0; i < 6; i++)
        {
            var player = new AudioStreamPlayer { Bus = "Master" };
            AddChild(player);
            _players.Add(player);
        }
    }

    public override void _ExitTree()
    {
        if (_instance == this) _instance = null;
    }

    /// <summary>Plays a sound if the sound system is up; a no-op otherwise (headless tools, spikes).
    /// The same sound retriggered within 40 ms is dropped, so a burst of events stays one sound.</summary>
    public static void Play(Sound sound, float volumeDb = 0f)
    {
        var sfx = _instance;
        if (sfx is null || !IsInstanceValid(sfx)) return;
        ulong now = Time.GetTicksMsec();
        if (sfx._lastPlayed.TryGetValue(sound, out ulong last) && now - last < 40) return;
        sfx._lastPlayed[sound] = now;
        var player = sfx._players[sfx._next];
        sfx._next = (sfx._next + 1) % sfx._players.Count;
        player.Stream = sfx._streams[sound];
        player.VolumeDb = volumeDb;
        player.Play();
    }

    // --- synthesis ------------------------------------------------------------------------------------

    private static float[] Synthesize(Sound sound) => sound switch
    {
        Sound.Click => Mix(0.04f, (t, rng) =>
            0.22f * Sine(1450f, t) * Decay(t, 0.006f) + 0.06f * Noise(rng) * Decay(t, 0.003f)),
        Sound.Select => Mix(0.12f, (t, _) =>
            0.16f * Sine(880f, t) * Decay(t, 0.045f) * Attack(t, 0.003f)
            + 0.10f * Sine(1320f, t) * Decay(t, 0.03f) * Attack(t, 0.003f)),
        Sound.Order => Mix(0.2f, (t, rng) =>
            0.36f * Sine(Glide(180f, 85f, t, 0.12f), t) * Decay(t, 0.07f) * Attack(t, 0.004f)
            + 0.07f * Noise(rng) * Decay(t, 0.035f)),
        Sound.EndTurn => Mix(1.4f, (t, _) =>
        {
            float env = Attack(t, 0.05f) * Decay(t, 0.42f);
            return env * (0.10f * Warm(196f, t) + 0.08f * Warm(247f, t) + 0.08f * Warm(294f, t) + 0.05f * Warm(392f, t));
        }),
        Sound.Bell => Mix(2.2f, (t, _) => Bell(659f, t) + 0.7f * Bell(988f, t - 0.22f)),
        Sound.Fanfare => Mix(2.6f, (t, _) =>
            0.8f * Bell(523f, t) + 0.75f * Bell(659f, t - 0.13f) + 0.8f * Bell(784f, t - 0.26f) + 0.6f * Bell(1047f, t - 0.42f)),
        Sound.War => Mix(1.1f, (t, rng) => Drum(t, rng) + 0.85f * Drum(t - 0.32f, rng) + 0.6f * Drum(t - 0.52f, rng)),
        Sound.Peace => Mix(1.6f, (t, _) => Flute(392f, t, 0.0f, 0.7f) + Flute(523f, t, 0.35f, 1.2f)),
        Sound.Error => Mix(0.16f, (t, _) =>
            0.14f * Triangle(150f, t) * Decay(t, 0.05f) * Attack(t, 0.005f)),
        _ => new float[1],
    };

    private static float[] Mix(float seconds, Func<float, Random, float> f)
    {
        int n = (int)(seconds * Rate);
        var samples = new float[n];
        var rng = new Random(1234); // fixed: the same sound every launch
        float lowpass = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float v = f(t, rng);
            // A gentle one-pole low-pass takes the edge off every synthetic tone.
            lowpass += (v - lowpass) * 0.55f;
            samples[i] = lowpass;
        }
        // Fade the last 10 ms so nothing ends in a click.
        int fade = Math.Min(n, Rate / 100);
        for (int i = 0; i < fade; i++) samples[n - 1 - i] *= i / (float)fade;
        return samples;
    }

    private static float Sine(float hz, float t) => MathF.Sin(MathF.Tau * hz * t);
    private static float Warm(float hz, float t) => Sine(hz, t) + 0.25f * Sine(hz * 2f, t) + 0.08f * Sine(hz * 3f, t);
    private static float Triangle(float hz, float t) => 2f * MathF.Abs(2f * (t * hz - MathF.Floor(t * hz + 0.5f))) - 1f;
    private static float Noise(Random rng) => (float)(rng.NextDouble() * 2.0 - 1.0);
    private static float Decay(float t, float tau) => t < 0 ? 0f : MathF.Exp(-t / tau);
    private static float Attack(float t, float seconds) => t < 0 ? 0f : MathF.Min(1f, t / seconds);

    /// <summary>A frequency sliding from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="seconds"/>, integrated so the phase stays continuous (no zipper).</summary>
    private static float Glide(float from, float to, float t, float seconds)
    {
        float k = MathF.Min(t, seconds) / seconds;
        // Average frequency over [0, t] for a linear glide, so Sine(freq, t) has the right phase.
        return from + (to - from) * k * 0.5f;
    }

    /// <summary>A struck bell: inharmonic partials, each dying away at its own rate.</summary>
    private static float Bell(float hz, float t)
    {
        if (t < 0) return 0f;
        float a = Attack(t, 0.002f);
        return a * (0.30f * Sine(hz, t) * Decay(t, 0.9f)
                    + 0.14f * Sine(hz * 2.0f, t) * Decay(t, 0.55f)
                    + 0.09f * Sine(hz * 3.01f, t) * Decay(t, 0.35f)
                    + 0.05f * Sine(hz * 4.2f, t) * Decay(t, 0.2f)
                    + 0.03f * Sine(hz * 5.4f, t) * Decay(t, 0.12f));
    }

    private static float Drum(float t, Random rng)
    {
        if (t < 0) return 0f;
        float body = Sine(Glide(95f, 45f, t, 0.14f), t) * Decay(t, 0.16f);
        return 0.55f * body * Attack(t, 0.002f) + 0.16f * Noise(rng) * Decay(t, 0.018f);
    }

    /// <summary>A soft flute-like note with a little vibrato, between <paramref name="start"/> and
    /// <paramref name="start"/> + <paramref name="length"/>.</summary>
    private static float Flute(float hz, float t, float start, float length)
    {
        float local = t - start;
        if (local < 0 || local > length + 0.5f) return 0f;
        float vib = 1f + 0.006f * MathF.Sin(MathF.Tau * 5f * local);
        float env = Attack(local, 0.08f) * (local < length ? 1f : Decay(local - length, 0.12f));
        return 0.13f * env * (Sine(hz * vib, local) + 0.2f * Sine(hz * 2f * vib, local));
    }

    private static AudioStreamWav ToWav(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Math.Clamp((int)(samples[i] * 32767f), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
            Data = bytes,
        };
    }

    /// <summary>Verification: the peak level of each synthesized sound, so a shell run can confirm
    /// every one exists, is audible and does not clip.</summary>
    public static string DumpPeaks()
    {
        var parts = new List<string>();
        foreach (Sound s in Enum.GetValues<Sound>())
        {
            var samples = Synthesize(s);
            float peak = 0f;
            foreach (var v in samples) peak = MathF.Max(peak, MathF.Abs(v));
            parts.Add($"{s}={peak:0.00}/{samples.Length / (float)Rate:0.00}s");
        }
        return "Sfx " + string.Join(" ", parts);
    }
}
