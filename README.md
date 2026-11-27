# Touchpad Lock

Lock your laptop touchpad with one click or shortcut, so palms, pets and little hands can't move the mouse.
The touchpad version of [Keyboard Lock](https://github.com/gsrkas101/keyboard-lock).

Windows 11 · single 50 KB exe · no install · no admin rights

![Touchpad locked pill](docs/locked.png)

## Features
- **Tray icon:** click it to lock or unlock. Green means unlocked, red and crossed out means locked.
- **Ctrl+Alt+T** locks and unlocks too, so you are never stuck without a mouse.
- A "Touchpad locked" pill springs open at the top of the screen, Dynamic Island style. **Double-click it with a mouse to unlock.** It turns green, then shrinks away.
- So it doesn't distract you while you work, the pill shrinks to a small icon after a moment. Point at it to see the full text.
- Drag the pill anywhere on the screen. It remembers the place; **Move the pill back to the top** in the menu resets it.
- Only the touchpad is blocked. Your mouse keeps working.
- The pill shows the real Windows touchpad state, so it also appears if you turn the touchpad off in Settings.
- Works together with Keyboard Lock: the two pills stack, and the touchpad can be unlocked while the keyboard is locked.
- Starts as soon as you sign in to Windows, using a Task Scheduler task. No admin rights needed.
- Command line options `--toggle`, `--lock`, `--unlock`, `--exit`, for a macro key or any launcher.

## Download
Get `TouchpadLock.exe` from [Releases](../../releases) and run it. The touchpad icon appears in the taskbar tray
(drag it out of the **^** menu if Windows hides it). It starts with Windows from then on; to turn that off, right-click
the icon and untick **Start with Windows**.

The exe isn't code-signed, so Windows SmartScreen may warn you: choose **More info → Run anyway**.
Or [build it yourself](#build-from-source). It needs .NET Framework 4.x, which is built into Windows.

## Safety
- No drivers and no device changes. It only uses Windows' own touchpad on/off switch.
- Exiting the app turns the touchpad back on. If the app crashes while locked, the touchpad turns back on the next time it starts.
- If the keyboard is also locked (by Keyboard Lock) and no mouse is connected, the touchpad unlocks itself.
- You can always turn the touchpad back on in **Settings → Bluetooth & devices → Touchpad**.

## Limits
- It needs a Windows Precision Touchpad, which most laptops made since about 2016 have.
- If Windows doesn't switch the touchpad while an app running as administrator is in front, click the desktop and try again.
- Ctrl+Alt+T can't be changed yet, and it replaces Word's ™ shortcut while the app is running.

## How it works
Windows has a built-in touchpad on/off shortcut, Ctrl+Win+F24, meant for laptop makers' touchpad keys. The app presses
it, then reads the real state back with `SPI_GETTOUCHPADPARAMETERS`, so it never guesses.

Keyboard Lock blocks all keys while it is locked, including ones typed by software. To get its own key presses
through, Touchpad Lock installs a low-level keyboard hook just for that moment and removes it right after. It never
records, saves or sends keystrokes, and you can read the source to check.

## Build from source
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```
This uses the C# compiler that ships with Windows, so no Visual Studio or SDK is needed. GitHub Actions builds every
push the same way and publishes the exe for tagged releases.

## Uninstalling
Right-click the icon and untick **Start with Windows**, then choose **Exit**. Then delete the exe
and the `%APPDATA%\TouchpadLock` folder.

## License
[MIT](LICENSE)
