using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SDL3;
using StarWars.Hardware;

namespace StarWars;

public sealed class SdlVectorDisplay : IDisposable
{
    private const float BeamWidthReferenceHeight = 540f;
    private const float MonitorBloomFadeSeconds = 0.14f;
    private const int MonitorOverdriveStartClocks = 48_000;
    private const int MonitorOverdriveFullClocks = 74_000;
    private const float MonitorAspectRatio = 4f / 3f;
    private const float MouseYokeSensitivity = 1.5f;
    private const byte YokeCenter = 0x80;

    private static readonly (float StartX, float StartY, float EndX, float EndY)[] PausedLabelStrokes =
    [
        // P
        (0, 6, 0, 0), (0, 0, 3, 0), (3, 0, 4, 1),
        (4, 1, 4, 2), (4, 2, 3, 3), (3, 3, 0, 3),
        // A
        (6, 6, 6, 1), (6, 1, 8, 0), (8, 0, 10, 1),
        (10, 1, 10, 6), (6, 3, 10, 3),
        // U
        (12, 0, 12, 5), (12, 5, 13, 6), (13, 6, 15, 6),
        (15, 6, 16, 5), (16, 5, 16, 0),
        // S
        (22, 0, 18, 0), (18, 0, 18, 3), (18, 3, 22, 3),
        (22, 3, 22, 6), (22, 6, 18, 6),
        // E
        (28, 0, 24, 0), (24, 0, 24, 6), (24, 6, 28, 6),
        (24, 3, 27, 3),
        // D
        (30, 0, 33, 0), (33, 0, 34, 1), (34, 1, 34, 5),
        (34, 5, 33, 6), (33, 6, 30, 6), (30, 6, 30, 0)
    ];

    private readonly nint _window;
    private readonly nint _renderer;
    private readonly nint _audioStream;
    private bool _yokeLeft;
    private bool _yokeRight;
    private bool _yokeUp;
    private bool _yokeDown;
    private bool _mouseYokeActive;
    private bool _spaceTriggerPressed;
    private bool _rightShiftTriggerPressed;
    private bool _leftMouseTriggerPressed;
    private bool _rightMouseTriggerPressed;
    private long _lastMonitorOverdrive;
    private float _lastOverdriveStrength;
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
        if (SDL.GetWindowSize(_window, out int windowWidth, out int windowHeight))
        {
            SDL.WarpMouseInWindow(_window, windowWidth * 0.5f, windowHeight * 0.5f);
        }

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

    public bool Paused { get; private set; }
    public bool SoundEnabled { get; private set; } = true;

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

            if (machine is not null
                && currentEvent.Type == (uint)SDL.EventType.MouseMotion)
            {
                SetMouseYoke(machine, currentEvent.Motion.X, currentEvent.Motion.Y);
                continue;
            }

            bool mouseButtonDown = currentEvent.Type == (uint)SDL.EventType.MouseButtonDown;
            bool mouseButtonUp = currentEvent.Type == (uint)SDL.EventType.MouseButtonUp;
            if (machine is not null && (mouseButtonDown || mouseButtonUp))
            {
                SetMouseButton(machine, currentEvent.Button.Button, mouseButtonDown);
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

            if (keyDown
                && !currentEvent.Key.Repeat
                && (currentEvent.Key.Mod & SDL.Keymod.Ctrl) != 0)
            {
                if (currentEvent.Key.Key == SDL.Keycode.P)
                {
                    Paused = !Paused;
                    if (Paused)
                    {
                        SDL.ClearAudioStream(_audioStream);
                    }

                    continue;
                }

                if (currentEvent.Key.Key == SDL.Keycode.Q)
                {
                    SoundEnabled = !SoundEnabled;
                    if (!SoundEnabled)
                    {
                        SDL.ClearAudioStream(_audioStream);
                    }

                    continue;
                }
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
                _mouseYokeActive = false;
                _yokeLeft = pressed;
                break;
            case SDL.Keycode.Right:
                _mouseYokeActive = false;
                _yokeRight = pressed;
                break;
            case SDL.Keycode.Up:
                _mouseYokeActive = false;
                _yokeUp = pressed;
                break;
            case SDL.Keycode.Down:
                _mouseYokeActive = false;
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
                _spaceTriggerPressed = pressed;
                UpdateTriggers(machine);
                break;
            case SDL.Keycode.RShift:
                _rightShiftTriggerPressed = pressed;
                UpdateTriggers(machine);
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
        if (_mouseYokeActive)
        {
            return;
        }

        machine.YokeYaw = MoveAxis(machine.YokeYaw, _yokeLeft, _yokeRight, elapsedSeconds, 360);
        machine.YokePitch = MoveAxis(machine.YokePitch, _yokeDown, _yokeUp, elapsedSeconds, 500);
    }

    private void SetMouseYoke(StarWarsMachine machine, float x, float y)
    {
        if (_yokeLeft || _yokeRight || _yokeUp || _yokeDown
            || !SDL.GetWindowSize(_window, out int windowWidth, out int windowHeight))
        {
            return;
        }

        GetMonitorViewport(
            windowWidth,
            windowHeight,
            out float viewportX,
            out float viewportY,
            out float viewportWidth,
            out float viewportHeight);

        float mouseX = (x - viewportX) / viewportWidth;
        float mouseY = (y - viewportY) / viewportHeight;
        float yaw = Math.Clamp(0.5f + (mouseX - 0.5f) * MouseYokeSensitivity, 0f, 1f);
        float pitch = Math.Clamp(0.5f - (mouseY - 0.5f) * MouseYokeSensitivity, 0f, 1f);
        machine.YokeYaw = (byte)MathF.Round(yaw * byte.MaxValue);
        machine.YokePitch = (byte)MathF.Round(pitch * byte.MaxValue);
        _mouseYokeActive = true;
    }

    private void SetMouseButton(StarWarsMachine machine, byte button, bool pressed)
    {
        if (button == SDL.ButtonLeft)
        {
            _leftMouseTriggerPressed = pressed;
        }
        else if (button == SDL.ButtonRight)
        {
            _rightMouseTriggerPressed = pressed;
        }
        else
        {
            return;
        }

        UpdateTriggers(machine);
    }

    private void UpdateTriggers(StarWarsMachine machine)
    {
        machine.LeftTriggerPressed = _spaceTriggerPressed || _leftMouseTriggerPressed;
        machine.RightTriggerPressed = _spaceTriggerPressed
            || _rightShiftTriggerPressed
            || _rightMouseTriggerPressed;
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
        _mouseYokeActive = false;
        _spaceTriggerPressed = false;
        _rightShiftTriggerPressed = false;
        _leftMouseTriggerPressed = false;
        _rightMouseTriggerPressed = false;
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

    public void Render(IReadOnlyList<VectorSegment> vectors) => Render(vectors, 0);

    public void Render(IReadOnlyList<VectorSegment> vectors, int overdriveBeamClocks)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        if (!SDL.GetCurrentRenderOutputSize(_renderer, out int outputWidth, out int outputHeight))
        {
            throw new InvalidOperationException($"SDL output-size query failed: {SDL.GetError()}");
        }

        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL.RenderClear(_renderer);

        GetMonitorViewport(
            outputWidth,
            outputHeight,
            out float offsetX,
            out float offsetY,
            out float viewportWidth,
            out float viewportHeight);

        // The AVG's 250 by 280 visible coordinates are deflected across a 4:3 CRT;
        // treating those coordinates as square pixels makes circles tall and narrow.
        float scaleX = viewportWidth / AtariStarWarsAvg.FieldWidth;
        float scaleY = viewportHeight / AtariStarWarsAvg.FieldHeight;
        int beamWidth = Math.Clamp(
            (int)MathF.Round(viewportHeight / BeamWidthReferenceHeight),
            1,
            8);

        float bloom = GetMonitorBloom(overdriveBeamClocks);
        if (bloom > 0)
        {
            var viewport = new SDL.Rect
            {
                X = (int)offsetX,
                Y = (int)offsetY,
                W = (int)viewportWidth,
                H = (int)viewportHeight
            };
            RenderMonitorBloom(vectors, in viewport, offsetX, offsetY, scaleX, scaleY, beamWidth, bloom);
        }

        float exposure = 1f + 0.09f * bloom;
        foreach (VectorSegment vector in vectors)
        {
            float brightness = Math.Min(1f, exposure * MathF.Sqrt(vector.Intensity / 255f));
            byte red = (byte)MathF.Round(vector.Red * brightness);
            byte green = (byte)MathF.Round(vector.Green * brightness);
            byte blue = (byte)MathF.Round(vector.Blue * brightness);
            SDL.SetRenderDrawColor(_renderer, red, green, blue, 255);

            float startX = offsetX + vector.StartX / 65536f * scaleX;
            float startY = offsetY + vector.StartY / 65536f * scaleY;
            float endX = offsetX + vector.EndX / 65536f * scaleX;
            float endY = offsetY + vector.EndY / 65536f * scaleY;
            RenderVectorLine(startX, startY, endX, endY, beamWidth);
        }

        if (Paused)
        {
            RenderPausedLabel(offsetX, offsetY, scaleX, scaleY, beamWidth);
        }

        SDL.RenderPresent(_renderer);
    }

    private float GetMonitorBloom(int overdriveBeamClocks)
    {
        if (Paused)
        {
            _lastOverdriveStrength = 0;
            return 0;
        }

        float strength = Math.Clamp(
            (overdriveBeamClocks - (float)MonitorOverdriveStartClocks)
                / (MonitorOverdriveFullClocks - MonitorOverdriveStartClocks),
            0,
            1);
        float remaining = 0;
        if (_lastOverdriveStrength > 0)
        {
            float recovery = Math.Max(
                0,
                1 - (float)Stopwatch.GetElapsedTime(_lastMonitorOverdrive).TotalSeconds / MonitorBloomFadeSeconds);
            remaining = _lastOverdriveStrength * recovery * recovery;
        }

        if (strength >= remaining)
        {
            _lastOverdriveStrength = strength;
            if (strength > 0)
            {
                _lastMonitorOverdrive = Stopwatch.GetTimestamp();
            }
        }

        return Math.Max(strength, remaining);
    }

    private void RenderMonitorBloom(
        IReadOnlyList<VectorSegment> vectors,
        in SDL.Rect viewport,
        float offsetX,
        float offsetY,
        float scaleX,
        float scaleY,
        int beamWidth,
        float bloom)
    {
        // A little stray light lifts the black level across the face of the CRT.
        var screen = new SDL.FRect { X = viewport.X, Y = viewport.Y, W = viewport.W, H = viewport.H };
        byte blackLevel = (byte)MathF.Round(14f * bloom);
        SDL.SetRenderDrawColor(_renderer, blackLevel, blackLevel, blackLevel, 255);
        SDL.RenderFillRect(_renderer, in screen);

        SDL.SetRenderClipRect(_renderer, in viewport);
        SDL.SetRenderDrawBlendMode(_renderer, SDL.BlendMode.Blend);

        // Taper the halo to zero around the beam and its end caps. Alpha
        // blending keeps connected strokes from adding up to bright blocks.
        float radius = 1.6f * beamWidth;
        Span<SDL.Vertex> halo = stackalloc SDL.Vertex[12];
        ReadOnlySpan<int> triangles =
        [
            0, 2, 3, 0, 3, 1,
            1, 3, 4, 1, 4, 5, 1, 5, 6, 1, 6, 7,
            0, 1, 7, 0, 7, 8,
            0, 8, 9, 0, 9, 10, 0, 10, 11, 0, 11, 2
        ];
        foreach (VectorSegment vector in vectors)
        {
            float alpha = 0.18f * bloom * MathF.Sqrt(vector.Intensity / 255f);
            if (alpha < 1f / 255f || (vector.Red | vector.Green | vector.Blue) == 0)
            {
                continue;
            }

            var start = new Vector2(offsetX + vector.StartX / 65536f * scaleX, offsetY + vector.StartY / 65536f * scaleY);
            var end = new Vector2(offsetX + vector.EndX / 65536f * scaleX, offsetY + vector.EndY / 65536f * scaleY);
            Vector2 delta = end - start;
            float length = delta.Length();
            Vector2 along = length < 0.001f ? new Vector2(radius, 0) : delta * (radius / length);
            var across = new Vector2(-along.Y, along.X);
            Vector2 diagonalA = (along + across) * 0.7071f;
            Vector2 diagonalB = (along - across) * 0.7071f;

            var color = new SDL.FColor
            {
                R = vector.Red / 255f,
                G = vector.Green / 255f,
                B = vector.Blue / 255f,
                A = alpha
            };

            halo[0] = BloomVertex(start, color);
            halo[1] = BloomVertex(end, color);
            color.A = 0;
            halo[2] = BloomVertex(start + across, color);
            halo[3] = BloomVertex(end + across, color);
            halo[4] = BloomVertex(end + diagonalA, color);
            halo[5] = BloomVertex(end + along, color);
            halo[6] = BloomVertex(end + diagonalB, color);
            halo[7] = BloomVertex(end - across, color);
            halo[8] = BloomVertex(start - across, color);
            halo[9] = BloomVertex(start - diagonalA, color);
            halo[10] = BloomVertex(start - along, color);
            halo[11] = BloomVertex(start - diagonalB, color);
            SDL.RenderGeometry(_renderer, nint.Zero, halo, halo.Length, triangles, triangles.Length);
        }

        SDL.SetRenderDrawBlendMode(_renderer, SDL.BlendMode.None);
        SDL.SetRenderClipRect(_renderer, nint.Zero);
    }

    private static SDL.Vertex BloomVertex(Vector2 point, SDL.FColor color) => new()
    {
        Position = new SDL.FPoint { X = point.X, Y = point.Y },
        Color = color
    };

    private void RenderPausedLabel(
        float offsetX,
        float offsetY,
        float scaleX,
        float scaleY,
        int beamWidth)
    {
        const float labelX = 108;
        const float labelY = 3;

        var background = new SDL.FRect
        {
            X = offsetX + (labelX - 1) * scaleX,
            Y = offsetY + (labelY - 1) * scaleY,
            W = 36 * scaleX,
            H = 8 * scaleY
        };
        SDL.SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL.RenderFillRect(_renderer, in background);

        SDL.SetRenderDrawColor(_renderer, 181, 0, 0, 255);
        foreach ((float startX, float startY, float endX, float endY) in PausedLabelStrokes)
        {
            RenderVectorLine(
                offsetX + (labelX + startX) * scaleX,
                offsetY + (labelY + startY) * scaleY,
                offsetX + (labelX + endX) * scaleX,
                offsetY + (labelY + endY) * scaleY,
                beamWidth);
        }
    }

    private void RenderVectorLine(
        float startX,
        float startY,
        float endX,
        float endY,
        int beamWidth)
    {
        if (beamWidth == 1)
        {
            SDL.RenderLine(_renderer, startX, startY, endX, endY);
            return;
        }

        float deltaX = endX - startX;
        float deltaY = endY - startY;
        float length = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (length < 0.001f)
        {
            RenderVectorCap(startX, startY, beamWidth);
            return;
        }

        float normalX = -deltaY / length;
        float normalY = deltaX / length;
        float firstOffset = -(beamWidth - 1) * 0.5f;
        for (int stroke = 0; stroke < beamWidth; stroke++)
        {
            float offset = firstOffset + stroke;
            float offsetX = normalX * offset;
            float offsetY = normalY * offset;
            SDL.RenderLine(
                _renderer,
                startX + offsetX,
                startY + offsetY,
                endX + offsetX,
                endY + offsetY);
        }

        // The physical beam has no flat line caps. Filling both endpoints also
        // prevents pinholes where thick vectors meet at sharply different angles.
        RenderVectorCap(startX, startY, beamWidth);
        RenderVectorCap(endX, endY, beamWidth);
    }

    private void RenderVectorCap(float x, float y, int beamWidth)
    {
        float halfWidth = (beamWidth - 1) * 0.5f;
        float firstOffset = -halfWidth;
        for (int row = 0; row < beamWidth; row++)
        {
            float rowY = y + firstOffset + row;
            SDL.RenderLine(_renderer, x - halfWidth, rowY, x + halfWidth, rowY);
        }
    }

    private static void GetMonitorViewport(
        int outputWidth,
        int outputHeight,
        out float x,
        out float y,
        out float width,
        out float height)
    {
        width = outputWidth;
        height = width / MonitorAspectRatio;
        if (height > outputHeight)
        {
            height = outputHeight;
            width = height * MonitorAspectRatio;
        }

        x = (outputWidth - width) * 0.5f;
        y = (outputHeight - height) * 0.5f;
    }

    public void QueueAudio(ReadOnlySpan<float> samples)
    {
        if (!SoundEnabled || samples.IsEmpty)
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
