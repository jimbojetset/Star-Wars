using System.Runtime.InteropServices;
using SDL3;
using StarWars.Hardware;

namespace StarWars;

public sealed class SdlVectorDisplay : IDisposable
{
    private const byte YokeCenter = 0x80;

    private readonly nint _window;
    private readonly nint _renderer;
    private readonly nint _audioStream;
    private bool _yokeLeft;
    private bool _yokeRight;
    private bool _yokeUp;
    private bool _yokeDown;
    private bool _disposed;

    public SdlVectorDisplay()
    {
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Audio))
        {
            throw new InvalidOperationException($"SDL initialization failed: {SDL.GetError()}");
        }

        SDL.WindowFlags flags = SDL.WindowFlags.Fullscreen | SDL.WindowFlags.HighPixelDensity;
        if (!SDL.CreateWindowAndRenderer("Star Wars Arcade Emulator", 1280, 720, flags, out _window, out _renderer))
        {
            SDL.Quit();
            throw new InvalidOperationException($"SDL window creation failed: {SDL.GetError()}");
        }

        SDL.SetRenderVSync(_renderer, 1);
        SDL.HideCursor();

        var audioSpec = new SDL.AudioSpec
        {
            Format = BitConverter.IsLittleEndian
                ? SDL.AudioFormat.AudioF32LE
                : SDL.AudioFormat.AudioF32BE,
            Channels = 1,
            Freq = StarWarsAudio.SampleRate
        };
        _audioStream = SDL.OpenAudioDeviceStream(
            SDL.AudioDeviceDefaultPlayback,
            in audioSpec,
            null!,
            nint.Zero);
        if (_audioStream == nint.Zero)
        {
            SDL.DestroyRenderer(_renderer);
            SDL.DestroyWindow(_window);
            SDL.Quit();
            throw new InvalidOperationException($"SDL audio initialization failed: {SDL.GetError()}");
        }

        if (!SDL.ResumeAudioStreamDevice(_audioStream))
        {
            SDL.DestroyAudioStream(_audioStream);
            SDL.DestroyRenderer(_renderer);
            SDL.DestroyWindow(_window);
            SDL.Quit();
            throw new InvalidOperationException($"SDL audio playback failed: {SDL.GetError()}");
        }
    }

    public bool ProcessEvents() => ProcessEventsCore(null, 0);

    public bool ProcessEvents(StarWarsMachine machine, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return ProcessEventsCore(machine, elapsedSeconds);
    }

    private bool ProcessEventsCore(StarWarsMachine? machine, double elapsedSeconds)
    {
        while (SDL.PollEvent(out SDL.Event currentEvent))
        {
            if (currentEvent.Type == (uint)SDL.EventType.Quit)
            {
                return false;
            }

            if (machine is not null
                && currentEvent.Type == (uint)SDL.EventType.WindowFocusLost)
            {
                ReleaseControls(machine);
                continue;
            }

            bool keyDown = currentEvent.Type == (uint)SDL.EventType.KeyDown;
            bool keyUp = currentEvent.Type == (uint)SDL.EventType.KeyUp;
            if (!keyDown && !keyUp)
            {
                continue;
            }

            if (keyDown && currentEvent.Key.Key == SDL.Keycode.Escape)
            {
                return false;
            }

            if (machine is not null)
            {
                SetKey(machine, currentEvent.Key.Key, keyDown, currentEvent.Key.Repeat);
            }
        }

        if (machine is not null)
        {
            UpdateYoke(machine, Math.Clamp(elapsedSeconds, 0, 0.05));
        }

        return true;
    }

    private void SetKey(StarWarsMachine machine, SDL.Keycode key, bool pressed, bool repeat)
    {
        switch (key)
        {
            case SDL.Keycode.Left:
                _yokeLeft = pressed;
                break;
            case SDL.Keycode.Right:
                _yokeRight = pressed;
                break;
            case SDL.Keycode.Up:
                _yokeUp = pressed;
                break;
            case SDL.Keycode.Down:
                _yokeDown = pressed;
                break;
            case SDL.Keycode.C:
                machine.LeftCoinPressed = pressed;
                break;
            case SDL.Keycode.V:
                machine.RightCoinPressed = pressed;
                break;
            case SDL.Keycode.Alpha1:
                machine.LeftThumbPressed = pressed;
                break;
            case SDL.Keycode.Alpha2:
                machine.RightThumbPressed = pressed;
                break;
            case SDL.Keycode.Space:
                machine.LeftTriggerPressed = pressed;
                break;
            case SDL.Keycode.RShift:
                machine.RightTriggerPressed = pressed;
                break;
            case SDL.Keycode.A:
                machine.AuxiliaryCoinPressed = pressed;
                break;
            case SDL.Keycode.T:
                machine.TiltPressed = pressed;
                break;
            case SDL.Keycode.F2:
                machine.DiagnosticStepPressed = pressed;
                break;
            case SDL.Keycode.F1 when pressed && !repeat:
                machine.SelfTestEnabled = !machine.SelfTestEnabled;
                break;
        }
    }

    private void UpdateYoke(StarWarsMachine machine, double elapsedSeconds)
    {
        machine.YokeYaw = MoveAxis(machine.YokeYaw, _yokeLeft, _yokeRight, elapsedSeconds, 360);
        machine.YokePitch = MoveAxis(machine.YokePitch, _yokeDown, _yokeUp, elapsedSeconds, 500);
    }

    private static byte MoveAxis(
        byte position,
        bool negativePressed,
        bool positivePressed,
        double elapsedSeconds,
        double speed)
    {
        if (negativePressed == positivePressed)
        {
            return YokeCenter;
        }

        int target = negativePressed ? 0x00 : 0xff;
        int distance = target - position;
        int movement = Math.Max(1, (int)Math.Round(speed * elapsedSeconds));
        return (byte)(position + Math.Clamp(distance, -movement, movement));
    }

    private void ReleaseControls(StarWarsMachine machine)
    {
        _yokeLeft = false;
        _yokeRight = false;
        _yokeUp = false;
        _yokeDown = false;
        machine.YokePitch = YokeCenter;
        machine.YokeYaw = YokeCenter;
        machine.LeftCoinPressed = false;
        machine.RightCoinPressed = false;
        machine.AuxiliaryCoinPressed = false;
        machine.TiltPressed = false;
        machine.LeftTriggerPressed = false;
        machine.RightTriggerPressed = false;
        machine.LeftThumbPressed = false;
        machine.RightThumbPressed = false;
        machine.DiagnosticStepPressed = false;
    }

    public void Render(IReadOnlyList<VectorSegment> vectors)
    {
        if (!SDL.GetCurrentRenderOutputSize(_renderer, out int outputWidth, out int outputHeight))
        {
            throw new InvalidOperationException($"SDL output-size query failed: {SDL.GetError()}");
        }

        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL.RenderClear(_renderer);

        float scale = Math.Min(
            outputWidth / (float)AtariStarWarsAvg.FieldWidth,
            outputHeight / (float)AtariStarWarsAvg.FieldHeight);
        float offsetX = (outputWidth - AtariStarWarsAvg.FieldWidth * scale) * 0.5f;
        float offsetY = (outputHeight - AtariStarWarsAvg.FieldHeight * scale) * 0.5f;

        foreach (VectorSegment vector in vectors)
        {
            float brightness = MathF.Sqrt(vector.Intensity / 255f);
            byte red = (byte)MathF.Round(vector.Red * brightness);
            byte green = (byte)MathF.Round(vector.Green * brightness);
            byte blue = (byte)MathF.Round(vector.Blue * brightness);
            SDL.SetRenderDrawColor(_renderer, red, green, blue, 255);

            float startX = offsetX + vector.StartX / 65536f * scale;
            float startY = offsetY + vector.StartY / 65536f * scale;
            float endX = offsetX + vector.EndX / 65536f * scale;
            float endY = offsetY + vector.EndY / 65536f * scale;
            SDL.RenderLine(_renderer, startX, startY, endX, endY);
        }

        SDL.RenderPresent(_renderer);
    }

    public void QueueAudio(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        int maximumQueuedBytes = StarWarsAudio.SampleRate * sizeof(float) / 4;
        if (SDL.GetAudioStreamQueued(_audioStream) > maximumQueuedBytes)
        {
            SDL.ClearAudioStream(_audioStream);
        }

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(samples);
        if (!SDL.PutAudioStreamData(_audioStream, bytes, bytes.Length))
        {
            throw new InvalidOperationException($"SDL audio queue failed: {SDL.GetError()}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SDL.DestroyAudioStream(_audioStream);
        SDL.DestroyRenderer(_renderer);
        SDL.DestroyWindow(_window);
        SDL.Quit();
        _disposed = true;
    }
}
