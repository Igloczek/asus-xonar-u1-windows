# XonarU1

Volume knob and LED control for the **ASUS Xonar U1** USB sound card on modern Windows.

ASUS dropped support for this card, but it still sounds great and plays fine on Microsoft's generic USB audio driver. What stops working is everything that relied on the old ASUS software: the knob does nothing, and the LED flashes constantly while audio plays. XonarU1 brings those back, with a modern Windows 11 style flyout on the tray icon.

## Features

- **Volume knob:** turning it changes the Windows volume, with the normal Windows flyout.
- **Mute:** pressing the knob toggles mute.
- **LED control:** on or off, blue, red or purple, and solid, blinking or reacting to audio.
- **Knob sensitivity:** low, medium or high.
- **Always on:** it starts with Windows like a driver would. No admin rights needed.

It doesn't install a driver or replace the one you have. Sound keeps playing through the Windows driver, and this app only talks to the card's control interface.

## Supported hardware

Tested on Windows 11 with the Xonar U1 reporting USB ID `0B05:1743`. To check yours, look in Device Manager for a device with `VID_0B05&PID_1743`.

The U1 also exists as `0B05:1739`. That version isn't supported yet because nobody has tested it. Reports are welcome.

## Install

1. Download `XonarU1.exe` from the Releases page and put it in any folder you like.
2. Run it. A small ring appears in the system tray, possibly under the `^` overflow arrow.
3. Click the icon to open the flyout. From now on it starts by itself at every logon.

To remove it, click the power button in the flyout, run `XonarU1.exe --uninstall`, then delete the exe. Settings are stored in `%APPDATA%\XonarU1\XonarU1.ini`.

The ring takes the colour of the LED, and turns grey with no pointer when the card isn't found.

## Build from source

No SDK needed. Windows includes the C# compiler this uses:

```bat
build.cmd
```

## Good to know

- Volume moves in Windows' fixed 2% media key steps. Sensitivity changes how far you turn per step, not the step size.
- The knob adjusts the current default output device, like keyboard volume keys do.
- Only the red LED can react to audio, so **On activity** isn't available for blue. Purple's blinking and on activity effects pulse the red LED over a steady blue one.
- Purple turns both LEDs on, but the red one is much stronger, so it looks close to red. The card has no brightness control, and fast blinking doesn't work as dimming because it blinks too slowly.
- The flyout's translucent background needs Windows 11 22H2 or later. Older versions get a solid panel. That fallback is untested.
- After unplugging the card, an app that was playing audio may stay silent until you restart it. That's Windows behaviour for USB audio, and the card itself recovers on its own.
- The app has to be running for the knob to work. The LED setting stays on the card until it loses power or resets.
- When an app running as administrator has focus, Windows may block the volume keys this app sends.

## How it works

The card has a HID interface with 17-byte reports. It sends no knob data until the host sets bit 7 of a configuration report, and the same report controls the LEDs. Nothing sends that report without the ASUS software, so the card stays in its default state.

XonarU1 sends that report, then turns knob movement into standard Windows media keys. The device code is in [XonarU1.cs](XonarU1.cs), and the flyout is in [Flyout.cs](Flyout.cs) and [Flyout.xaml](Flyout.xaml).

## Credits

The protocol was reverse engineered by Alessandro Pignotti for [xonard](https://github.com/alexp-sssup/asus-xonar-u1-utils), the Linux daemon for this card. Stephane Albert's xonarctl documented the LED modes.

This project isn't affiliated with or endorsed by ASUS.

## License

[GPL-3.0-or-later](LICENSE)
