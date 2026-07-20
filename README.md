# g600-key-remap-daemon

Remaps G9-G20 on a Logitech G600 to F13-F24 **without writing anything to the
mouse's onboard memory**. GHub itself only applies this kind of mapping in
software (it declines to persist arbitrary keyboard keys to onboard hardware
profiles) — this does the same thing on Linux: grab the G600's keyboard-
emulation HID interface, translate its default G9-G20 keys (`1234567890-=`)
to F13-F24 on the fly, and re-emit everything through a virtual keyboard.
Anything else that interface sends (e.g. G7's shift+B) passes through
unchanged. Nothing here touches flash/EEPROM — stopping the process or
unplugging the mouse undoes it completely and instantly.

## Importing into your own flake

```nix
{
  inputs.g600-key-remap-daemon.url = "github:Krutonium/G600-key-remap-daemon"; # or a git URL

  outputs = { self, nixpkgs, g600-key-remap-daemon, ... }: {
    nixosConfigurations.somehost = nixpkgs.lib.nixosSystem {
      modules = [
        g600-key-remap-daemon.nixosModules.g600-key-remap
        {
          services.g600-key-remap.enable = true;
          # package defaults to this flake's own build — only set it if you want
          # to override with something else.
        }
      ];
    };
  };
}
```

## Build

```fish
nix develop
dotnet build
```

## Try it first, safely

```fish
sudo dotnet run -- --list
```

This lists every `/dev/input/eventN` device, flagging the one that looks
like the G600's keyboard interface (matches vendor/product, supports
`EV_KEY`, does *not* support `EV_REL`). Confirm it picked the right one
before running for real:

```fish
sudo dotnet run
```

Press G9-G20 and confirm F13-F24 come through (`xev`/`wev`/`showkey` are
good for checking without needing an app that binds those keys). Ctrl+C
releases the device cleanly.

If auto-detection picks the wrong interface, override it:

```fish
sudo dotnet run -- --device /dev/input/event7
```

## Running it permanently

Just `services.g600-key-remap.enable = true;` via the module import shown
above — it turns on `hardware.uinput.enable` for you and builds the daemon
from this flake's own `packages.default`.

## Notes

- `SourceKeys`/`TargetKeys` in `Program.cs` are the only things you'd need
  to edit to remap something else — everything else is generic pass-through.
- Runs as root in the systemd unit for simplicity (needs to open whichever
  `/dev/input/eventN` the G600's keyboard interface lands on, plus
  `/dev/uinput`). If you'd rather not, you can lock it down with udev rules
  granting a dedicated group access to both, and drop `User=`/`Group=` into
  the systemd unit instead.
