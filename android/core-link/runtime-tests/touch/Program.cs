using System;
using S2Arm.Platforms.Android;
using S2A.Services;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.Common;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.Gesture;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

static void Check(bool condition) { if (!condition) throw new Exception("touch assertion failed"); }
var state = new TouchHidState();
Check(state.Apply(new uint[] { 29, 65535, 65535, 0, 0, 4, 0, 0, 0, 0 }));
Check(state.Ids[0] == 29 && state.Points[0].X == 1279 && state.Points[0].Y == 719);
Check(state.Points[0].Attribute == TouchAttribute.Start);
state.Held(); Check(state.Points[0].Attribute == TouchAttribute.None);
Check(state.Apply(new uint[] {4, 20, 30, 0, 0}));
Check(state.Ids[0] == 4 && state.Points[0].Attribute == TouchAttribute.None);
Check(state.Ids[1] == 29 && state.Points[1].Attribute == TouchAttribute.End);
state.Held(); Check(state.Ids.Length == 1 && state.Ids[0] == 4);
Check(state.Apply(Array.Empty<uint>())); Check(state.Points[0].Attribute == TouchAttribute.End);
state.Held(); Check(state.Points.Length == 0);
uint[] first = new uint[80], second = new uint[80];
for (int i = 0; i < 16; i++) { first[i * 5] = (uint)i; second[i * 5] = (uint)i + 16; }
Check(state.Apply(first)); Check(!state.Apply(second));
Check(state.Points.Length == 16 && state.Points[15].Attribute == TouchAttribute.End);
Check(state.Apply(second)); Check(state.Ids[0] == 16 && state.Points[0].Attribute == TouchAttribute.Start);
state.Cancel();
Check(state.Points.Length == 16 && state.Ids.Length == 16);
for (int i = 0; i < 16; i++)
    Check(state.Ids[i] == (uint)i + 16 && state.Points[i].Attribute == TouchAttribute.End);
state.Held(); Check(state.Points.Length == 0);
state.Cancel(); Check(state.Points.Length == 0);
Check(state.Apply(new uint[] {16, 65535, 65535, 0, 0}));
Check(state.Points[0].Attribute == TouchAttribute.Start);
state.Cancel();
Check(state.Points.Length == 1 && state.Ids[0] == 16 && state.Points[0].Attribute == TouchAttribute.End && state.Points[0].X == 1279 && state.Points[0].Y == 719);
state.Held(); Check(state.Points.Length == 0);
bool rejected = false;
try { state.Apply(new uint[] {1, 65536, 0, 0, 0}); } catch (ArgumentException) { rejected = true; }
Check(rejected);
rejected = false;
try { state.Apply(new uint[] {1, 0, 0, 0, 0, 1, 0, 0, 0, 0}); } catch (ArgumentException) { rejected = true; }
Check(rejected);
Console.WriteLine("touch HID: stable IDs, endpoints, Start/End/held, cancel, 16-slot replacement and invalid input passed");

Check(Unsafe.SizeOf<GestureInput>() == 0x58);
Check(Unsafe.SizeOf<GestureState>() == 0x60);
Check(Unsafe.SizeOf<RingLifo<GestureState>>() == 0x708);
const int offset = 0x3BA00;
Check(Marshal.OffsetOf<SharedMemory>(nameof(SharedMemory.Gesture)).ToInt32() == offset);
byte[] hidBytes = new byte[0x40000];
MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()) = SharedMemory.Create();
Span<byte> bytes = hidBytes;
Check(bytes.Slice(offset, 0x708).IndexOfAnyExcept((byte)0) < 0); // default path writes nothing here
byte[] before = bytes.ToArray();
MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture = RingLifo<GestureState>.Create();
ulong sample = 0;
int unsupported = 0;
void Write(GestureInput input) => GestureDevice.Write(ref MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture, ref sample, in input);
var gesture = new TouchGestureState(Write, _ => unsupported++);
uint[] contact = { 7, 65535, 0, 0, 0 };
gesture.Feed(contact, 1_000_000_000);
gesture.Feed(Array.Empty<uint>(), 1_180_000_000); // queued together, 180 ms source tap
var entries = MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.ReadEntries(16);
Check(entries.Length == 3);
Check(entries[0].Object.Input.Type == GestureType.Complete);
Check(entries[1].Object.Input.Type == GestureType.Tap);
Check(entries[2].Object.Input.Type == GestureType.Touch);
Check(entries[1].Object.Input.X == 1279 && entries[1].Object.Input.Y == 0);
Check(entries[1].Object.Input.ContextNumber == entries[2].Object.Input.ContextNumber);
Check(MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.ReadEntries(16).Length == 3); // repeated SDK reads cannot consume Tap
Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8)) == 17);
Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 24, 8)) == 3);
int tap = offset + 32 + 2 * 104;
Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(tap, 8)) == 4); // AtomicStorage uses sample << 1
Check(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(tap + 8, 8)) == 2);
Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(tap + 24, 4)) == 5);
Check(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(tap + 32, 4)) == 1279);
Check(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(tap + 68, 4)) == 1);
Check(bytes[..offset].SequenceEqual(before.AsSpan(0, offset)));
Check(bytes[(offset + 0x708)..].SequenceEqual(before.AsSpan(offset + 0x708)));

MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.Clear();
gesture.Feed(contact, 2_000_000_000);
gesture.Tick(2_600_000_000);
ulong afterPress = sample;
gesture.Tick(2_700_000_000); Check(sample == afterPress);
gesture.Feed(Array.Empty<uint>(), 2_800_000_000);
entries = MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.ReadEntries(16);
Check(entries.Length == 3 && entries[1].Object.Input.Type == GestureType.Press);
MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.Clear();
gesture.Feed(new uint[] { 7, 0, 0, 0, 0 }, 3_000_000_000);
gesture.Feed(new uint[] { 7, 32768, 0, 0, 0 }, 3_100_000_000);
gesture.Feed(Array.Empty<uint>(), 3_200_000_000);
entries = MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.ReadEntries(16);
Check(entries.Length == 3 && entries[1].Object.Input.Type == GestureType.Pan);
Check(entries[1].Object.Input.DeltaX > 0 && float.IsFinite(entries[1].Object.Input.VelocityX));
MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.Clear();
gesture.Feed(contact, 4_000_000_000);
gesture.Feed(new uint[] {7, 1, 1, 0, 0, 8, 2, 2, 0, 0}, 4_100_000_000);
gesture.Feed(Array.Empty<uint>(), 4_200_000_000);
entries = MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.ReadEntries(16);
Check(unsupported == 1 && entries.Length == 2 && entries[0].Object.Input.Type == GestureType.Cancel);
gesture.Feed(contact, 5_000_000_000); gesture.Cancel();
Check(MemoryMarshal.AsRef<SharedMemory>(hidBytes.AsSpan()).Gesture.ReadEntries(1)[0].Object.Input.Type == GestureType.Cancel);
bool badTime = false;
try { gesture.Feed(contact, 1); } catch (ArgumentException) { badTime = true; }
Check(badTime);
Console.WriteLine("gesture HID: real RingLifo/AtomicStorage writer, 0x60 state/0x708 ring/0x3BA00 offset, persistent Tap history, source time, Press/Pan, cancel and unsupported multi-contact passed");

uint[] moving = { 7, 1, 1, 0, 0 };
ulong now = 6_000_000_000;
gesture.Feed(moving, now);
for (int i = 0; i < 512; i++) { moving[1] = (uint)(i * 97 % 65536); gesture.Feed(moving, now += 10_000_000); }
long allocated = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1024; i++) { moving[1] = (uint)(i * 97 % 65536); gesture.Feed(moving, now += 10_000_000); }
allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
Check(allocated == 0);
Console.WriteLine("gesture recognizer + real HID ring steady moves: 0 managed bytes / 1024 samples (host mechanism test only)");
