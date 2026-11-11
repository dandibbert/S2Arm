using System;
using Ryujinx.HLE.HOS.Services.Hid;

namespace S2A.Services;

// 触屏 → HID 手势区（单点：Touch / Tap / Press / Pan；多点直接取消，捏合 / 旋转没做）。按触点来源的单调时间判，不按取出时间。
//   原在 android/core-runtime（只有安卓写手势区）。#568：太阁立志传Ⅴ DX 调 ActivateGesture、读手势区，苹果端从没写过这块，
//   纯触屏下点什么都没反应；挪到服务层后 iOS 壳 / Mac 窗口（InputPump.PushTouch）与安卓共用同一个识别器。
//   安卓喂 Feed（每点 5 个数、坐标归一到 0–65535）；苹果喂 FeedContacts（坐标已是游戏触屏空间，原样写）。原始触屏区不归这里管。
public sealed class TouchGestureState
{
    private readonly Action<GestureInput> _write;
    private readonly Action<string> _unsupported;
    private bool _active, _pan, _press, _blocked;
    private uint _id, _x, _y, _startX, _startY;
    private ulong _context, _startNs, _lastNs;
    public const ulong PressNs = 500_000_000;
    public const long SlopSquared = 100;

    public TouchGestureState(Action<GestureInput> write, Action<string> unsupported)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _unsupported = unsupported ?? throw new ArgumentNullException(nameof(unsupported));
    }
    private void Emit(GestureType type, int dx = 0, int dy = 0, ulong elapsed = 0)
    {
        bool points = type is GestureType.Touch or GestureType.Press or GestureType.Pan or GestureType.Tap;
        _write(new GestureInput {
            ContextNumber = _context, Type = type, X = _x, Y = _y,
            DeltaX = dx, DeltaY = dy,
            VelocityX = elapsed == 0 ? 0 : dx * (1_000_000_000f / elapsed),
            VelocityY = elapsed == 0 ? 0 : dy * (1_000_000_000f / elapsed),
            Attributes = type == GestureType.Touch ? 1u << 4 : 0,
            Scale = 1, PointCount = points ? 1 : 0,
            Point0 = points ? new GesturePoint { X = _x, Y = _y } : default,
        });
    }
    public void Cancel()
    {
        if (_active) Emit(GestureType.Cancel);
        _active = _pan = _press = _blocked = false;
    }
    // 安卓：每点 5 个数（id、x、y 归一到 0–65535、其余两项），最多 16 点
    public void Feed(ReadOnlySpan<uint> raw, ulong ns)
    {
        if (raw.Length % 5 != 0 || raw.Length > 80 || ns == 0 || ns < _lastNs)
            throw new ArgumentException("Invalid gesture frame or regressing source time.");
        if (raw.Length == 5)
            for (int i = 1; i < 5; i++) if (raw[i] > 65535) throw new ArgumentException("Gesture coordinate out of range.");
        int count = raw.Length / 5;
        FeedContacts(count, count == 0 ? 0 : raw[0], count == 0 ? 0 : (uint)((ulong)raw[1] * 1279 / 65535), count == 0 ? 0 : (uint)((ulong)raw[2] * 719 / 65535), ns);
    }
    // 这一刻按着的触点数与（只有一点时）它的 id、坐标（游戏触屏空间）
    public void FeedContacts(int count, uint id, uint x, uint y, ulong ns)
    {
        if (count < 0 || ns == 0 || ns < _lastNs) throw new ArgumentException("Invalid gesture frame or regressing source time.");
        if (count > 1) {
            bool report = !_blocked;
            Cancel(); _blocked = true; _lastNs = ns;
            if (report) _unsupported("Multi-contact gestures (pinch/rotate) are not implemented; gesture stream cancelled.");
            return;
        }
        if (_blocked) { if (count == 0) _blocked = false; _lastNs = ns; return; }
        if (count == 0) {
            if (_active) {
                Tick(ns);
                if (!_pan && !_press && ns - _startNs < PressNs) Emit(GestureType.Tap);
                Emit(GestureType.Complete);
                _active = false;
            }
            _lastNs = ns;
            return;
        }
        if (_active && id != _id) Cancel();
        if (!_active) {
            _active = true; _pan = _press = false;
            _id = id; _x = _startX = x; _y = _startY = y; _startNs = ns;
            if (_context == ulong.MaxValue) throw new InvalidOperationException("Gesture context overflow.");
            _context++; Emit(GestureType.Touch);
        } else {
            long sx = (long)x - _startX, sy = (long)y - _startY;
            _pan |= sx * sx + sy * sy > SlopSquared;
            int dx = (int)x - (int)_x, dy = (int)y - (int)_y;
            _x = x; _y = y;
            if (_pan && (dx != 0 || dy != 0)) Emit(GestureType.Pan, dx, dy, ns - _lastNs);
            else Tick(ns);
        }
        _lastNs = ns;
    }
    public void Tick(ulong nowNs)
    {
        if (_active && !_pan && !_press && nowNs >= _startNs && nowNs - _startNs >= PressNs) {
            Emit(GestureType.Press); _press = true;
        }
    }
}
