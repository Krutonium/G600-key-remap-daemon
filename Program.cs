using System.Runtime.InteropServices;

namespace G600KeyRemapDaemon;

/// <summary>
/// Remaps G9-G20 on a Logitech G600 to F13-F24 without touching the mouse's onboard
/// memory. The G600 exposes a "keyboard emulation" USB HID interface that sends the
/// default G9-G20 keys (1234567890-=) plus G7's shift+B — this grabs that interface
/// exclusively, translates the 12 default keys to F13-F24 on the fly, and re-emits
/// everything (translated or not) through a virtual uinput keyboard. Nothing here is
/// persisted to the mouse; killing this process (or unplugging the mouse) undoes it
/// instantly and completely.
/// </summary>
internal static class Program
{
    private const ushort VendorId = 0x046d;
    private const ushort ProductId = 0xc24a;

    // Default G9-G20 keycodes (linux/input-event-codes.h): KEY_1..KEY_0, KEY_MINUS, KEY_EQUAL.
    private static readonly ushort[] SourceKeys = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

    // KEY_F13..KEY_F24 (183..194), same order as SourceKeys (G9->F13 ... G20->F24).
    private static readonly ushort[] TargetKeys = { 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194 };

    private static readonly Dictionary<ushort, ushort> RemapTable =
        SourceKeys.Zip(TargetKeys, (src, dst) => (src, dst)).ToDictionary(p => p.src, p => p.dst);

    private static int _sourceFd = -1;
    private static int _uinputFd = -1;
    private static volatile bool _running = true;

    private static int Main(string[] args)
    {
        if (args.Contains("--list"))
        {
            ListCandidateDevices();
            return 0;
        }

        string? explicitPath = null;
        int explicitIdx = Array.IndexOf(args, "--device");
        if (explicitIdx >= 0 && explicitIdx + 1 < args.Length)
        {
            explicitPath = args[explicitIdx + 1];
        }

        string? devicePath = explicitPath ?? FindKeyboardInterface();
        if (devicePath == null)
        {
            Console.Error.WriteLine(
                "Couldn't auto-detect the G600's keyboard-emulation interface. " +
                "Run with --list to see every candidate, then re-run with --device /dev/input/eventN.");
            return 1;
        }

        Console.WriteLine($"Using source device: {devicePath}");
        return RunRemap(devicePath);
    }

    private static int RunRemap(string devicePath)
    {
        _sourceFd = LinuxInput.open(devicePath, LinuxInput.O_RDONLY);
        if (_sourceFd < 0)
        {
            Console.Error.WriteLine($"Couldn't open {devicePath} (errno {Marshal.GetLastWin32Error()}). Try running as root.");
            return 1;
        }

        byte[] keyBits = QueryKeyBits(_sourceFd);

        _uinputFd = LinuxInput.open("/dev/uinput", LinuxInput.O_WRONLY | LinuxInput.O_NONBLOCK);
        if (_uinputFd < 0)
        {
            Console.Error.WriteLine($"Couldn't open /dev/uinput (errno {Marshal.GetLastWin32Error()}). Try running as root.");
            LinuxInput.close(_sourceFd);
            return 1;
        }

        SetUpVirtualKeyboard(_uinputFd, keyBits);

        if (LinuxInput.ioctl_int(_sourceFd, LinuxInput.EVIOCGRAB, 1) < 0)
        {
            Console.Error.WriteLine($"Couldn't grab {devicePath} exclusively (errno {Marshal.GetLastWin32Error()}).");
            Cleanup();
            return 1;
        }

        Console.WriteLine("Remapping G9-G20 -> F13-F24. Ctrl+C or SIGTERM to stop and release the device cleanly.");

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            _running = false;
        });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            _running = false;
        });

        var buf = new byte[LinuxInput.InputEventSize];
        while (_running)
        {
            long n = LinuxInput.read(_sourceFd, buf, buf.Length);
            if (n != buf.Length)
            {
                if (!_running)
                {
                    break;
                }
                // Device likely unplugged.
                Console.Error.WriteLine("Lost the source device (unplugged?). Exiting.");
                break;
            }

            var (type, code, value) = LinuxInput.ParseInputEvent(buf);
            if (type == LinuxInput.EV_KEY && RemapTable.TryGetValue(code, out ushort mapped))
            {
                LinuxInput.WriteInputEvent(buf, type, mapped, value);
            }

            LinuxInput.write(_uinputFd, buf, buf.Length);
        }

        Cleanup();
        return 0;
    }

    private static void Cleanup()
    {
        if (_sourceFd >= 0)
        {
            LinuxInput.ioctl_int(_sourceFd, LinuxInput.EVIOCGRAB, 0);
            LinuxInput.close(_sourceFd);
            _sourceFd = -1;
        }

        if (_uinputFd >= 0)
        {
            LinuxInput.ioctl_int(_uinputFd, LinuxInput.UI_DEV_DESTROY, 0);
            LinuxInput.close(_uinputFd);
            _uinputFd = -1;
        }
    }

    /// <summary>Reads the source device's supported-key bitmap (EVIOCGBIT on EV_KEY).</summary>
    private static byte[] QueryKeyBits(int fd)
    {
        var buf = new byte[(LinuxInput.KEY_MAX / 8) + 8]; // generous, kernel truncates as needed
        LinuxInput.ioctl_buf(fd, LinuxInput.EVIOCGBIT(LinuxInput.EV_KEY, (uint)buf.Length), buf);
        return buf;
    }

    private static bool KeyBitSet(byte[] keyBits, int code) =>
        (keyBits[code / 8] & (1 << (code % 8))) != 0;

    /// <summary>
    /// Mirrors the source device's key capabilities onto the virtual device (so anything
    /// it can legitimately send — like G7's shift+B — still works once forwarded), and
    /// makes sure F13-F24 are enabled even if the source didn't happen to report them.
    /// </summary>
    private static void SetUpVirtualKeyboard(int uinputFd, byte[] keyBits)
    {
        LinuxInput.ioctl_int(uinputFd, LinuxInput.UI_SET_EVBIT, LinuxInput.EV_KEY);

        for (int code = 0; code <= LinuxInput.KEY_MAX; code++)
        {
            if (KeyBitSet(keyBits, code))
            {
                LinuxInput.ioctl_int(uinputFd, LinuxInput.UI_SET_KEYBIT, code);
            }
        }

        foreach (ushort target in TargetKeys)
        {
            LinuxInput.ioctl_int(uinputFd, LinuxInput.UI_SET_KEYBIT, target);
        }

        byte[] devBuf = LinuxInput.BuildUinputUserDev("G600 G9-G20 remap (virtual)");
        LinuxInput.write(uinputFd, devBuf, devBuf.Length);
        LinuxInput.ioctl_int(uinputFd, LinuxInput.UI_DEV_CREATE, 0);
    }

    /// <summary>
    /// Scans /dev/input/eventN for the G600's keyboard-emulation interface: matching
    /// vendor/product, supports EV_KEY, and does NOT support EV_REL (that's the mouse
    /// movement interface, not the one we want).
    /// </summary>
    private static string? FindKeyboardInterface()
    {
        foreach (var (path, vendor, product, hasKey, hasRel) in EnumerateCandidates())
        {
            if (vendor == VendorId && product == ProductId && hasKey && !hasRel)
            {
                return path;
            }
        }

        return null;
    }

    private static void ListCandidateDevices()
    {
        foreach (var (path, vendor, product, hasKey, hasRel) in EnumerateCandidates())
        {
            string match = vendor == VendorId && product == ProductId
                ? (hasKey && !hasRel ? "  <-- looks like the keyboard interface" : "")
                : "";
            Console.WriteLine($"{path}: vid={vendor:x4} pid={product:x4} EV_KEY={hasKey} EV_REL={hasRel}{match}");
        }
    }

    private static IEnumerable<(string path, ushort vendor, ushort product, bool hasKey, bool hasRel)> EnumerateCandidates()
    {
        for (int i = 0; i < 32; i++)
        {
            string path = $"/dev/input/event{i}";
            if (!File.Exists(path))
            {
                continue;
            }

            int fd = LinuxInput.open(path, LinuxInput.O_RDONLY);
            if (fd < 0)
            {
                continue;
            }

            try
            {
                var idBuf = new byte[8];
                if (LinuxInput.ioctl_buf(fd, LinuxInput.EVIOCGID, idBuf) < 0)
                {
                    continue;
                }

                ushort vendor = BitConverter.ToUInt16(idBuf, 2);
                ushort product = BitConverter.ToUInt16(idBuf, 4);

                var typeBits = new byte[8];
                LinuxInput.ioctl_buf(fd, LinuxInput.EVIOCGBIT(0, (uint)typeBits.Length), typeBits);
                bool hasKey = (typeBits[LinuxInput.EV_KEY / 8] & (1 << (LinuxInput.EV_KEY % 8))) != 0;
                bool hasRel = (typeBits[LinuxInput.EV_REL / 8] & (1 << (LinuxInput.EV_REL % 8))) != 0;

                yield return (path, vendor, product, hasKey, hasRel);
            }
            finally
            {
                LinuxInput.close(fd);
            }
        }
    }
}
