using System.Runtime.InteropServices;

namespace G600KeyRemapDaemon;

/// <summary>
/// Minimal raw bindings against evdev (/dev/input/eventN) and uinput (/dev/uinput).
/// No libevdev dependency — just the same ioctls libevdev itself wraps.
/// </summary>
internal static class LinuxInput
{
    // open() flags
    public const int O_RDONLY = 0;
    public const int O_WRONLY = 1;
    public const int O_NONBLOCK = 0x800;

    // evdev event types (linux/input-event-codes.h)
    public const ushort EV_SYN = 0x00;
    public const ushort EV_KEY = 0x01;
    public const ushort EV_REL = 0x02;
    public const ushort EV_MAX = 0x1f;
    public const ushort KEY_MAX = 0x2ff;

    [DllImport("libc", SetLastError = true)]
    public static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern long read(int fd, byte[] buf, long count);

    [DllImport("libc", SetLastError = true)]
    public static extern long write(int fd, byte[] buf, long count);

    // Two overloads of the same native ioctl(): one for plain-int args (UI_SET_EVBIT,
    // UI_SET_KEYBIT, UI_DEV_CREATE, EVIOCGRAB), one for buffer args (EVIOCGID, EVIOCGBIT).
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    public static extern int ioctl_int(int fd, uint request, int arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    public static extern int ioctl_buf(int fd, uint request, byte[] arg);

    // --- _IOC request-code builder, mirrors <asm-generic/ioctl.h> exactly ---
    private const uint IOC_NONE = 0;
    private const uint IOC_WRITE = 1;
    private const uint IOC_READ = 2;

    private static uint IOC(uint dir, char type, uint nr, uint size) =>
        (dir << 30) | (size << 16) | ((uint)type << 8) | nr;

    private static uint IO(char type, uint nr) => IOC(IOC_NONE, type, nr, 0);
    private static uint IOR(char type, uint nr, uint size) => IOC(IOC_READ, type, nr, size);
    private static uint IOW(char type, uint nr, uint size) => IOC(IOC_WRITE, type, nr, size);

    // evdev ioctls
    public static readonly uint EVIOCGRAB = IOW('E', 0x90, 4);              // int
    public static readonly uint EVIOCGID = IOR('E', 0x02, 8);               // struct input_id (4x u16)
    public static uint EVIOCGBIT(int ev, uint len) => IOR('E', (uint)(0x20 + ev), len);

    // uinput ioctls
    public static readonly uint UI_SET_EVBIT = IOW('U', 100, 4);            // int
    public static readonly uint UI_SET_KEYBIT = IOW('U', 101, 4);           // int
    public static readonly uint UI_DEV_CREATE = IO('U', 1);
    public static readonly uint UI_DEV_DESTROY = IO('U', 2);

    /// <summary>Raw struct input_event, 24 bytes on 64-bit Linux (16-byte timeval + 2+2+4).</summary>
    public const int InputEventSize = 24;

    public static (ushort type, ushort code, int value) ParseInputEvent(byte[] buf)
    {
        ushort type = BitConverter.ToUInt16(buf, 16);
        ushort code = BitConverter.ToUInt16(buf, 18);
        int value = BitConverter.ToInt32(buf, 20);
        return (type, code, value);
    }

    /// <summary>Rewrites just the type/code/value of a 24-byte input_event buffer in place.</summary>
    public static void WriteInputEvent(byte[] buf, ushort type, ushort code, int value)
    {
        BitConverter.GetBytes(type).CopyTo(buf, 16);
        BitConverter.GetBytes(code).CopyTo(buf, 18);
        BitConverter.GetBytes(value).CopyTo(buf, 20);
    }

    /// <summary>
    /// Builds the legacy struct uinput_user_dev payload written to /dev/uinput before
    /// UI_DEV_CREATE: char name[80]; struct input_id id (4x u16); u32 ff_effects_max;
    /// s32 absmax[64]; absmin[64]; absfuzz[64]; absflat[64]. We only need name/id — the
    /// abs arrays are left zeroed since this is a keys-only virtual device.
    /// </summary>
    public static byte[] BuildUinputUserDev(string name)
    {
        var buf = new byte[80 + 8 + 4 + 4 * 64 * 4];
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, buf, Math.Min(nameBytes.Length, 79));

        const ushort BUS_VIRTUAL = 0x06;
        BitConverter.GetBytes(BUS_VIRTUAL).CopyTo(buf, 80); // bustype
        BitConverter.GetBytes((ushort)0x1234).CopyTo(buf, 82); // vendor
        BitConverter.GetBytes((ushort)0x5678).CopyTo(buf, 84); // product
        BitConverter.GetBytes((ushort)1).CopyTo(buf, 86);      // version
        // ff_effects_max and all abs arrays stay zero.
        return buf;
    }
}
