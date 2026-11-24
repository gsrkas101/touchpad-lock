// Touchpad Lock - turns the laptop touchpad off and on, with a "Touchpad locked" pill at the top
// of the screen and a tray button. Sibling of Keyboard Lock, same look.
//
// How it works: Windows has a built-in touchpad on/off shortcut meant for laptop makers' touchpad
// keys (Ctrl+Win+F24). The app presses it and reads the real state back, so the pill always shows
// the truth, even if the touchpad was turned off by a laptop key or in Windows Settings.
//
// Safe by design: no drivers, no admin rights, no device changes. Mice and the touch screen are
// never blocked. Exiting turns the touchpad back on, and so does the next start after a crash.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Touchpad Lock")]
[assembly: System.Reflection.AssemblyProduct("Touchpad Lock")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace TouchpadLock
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--export-icon")
            {
                Icons.ExportIco(args[1]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--enable-autostart")
            {
                Autostart.Enable();
                return 0;
            }
            string command = ParseCommand(args);
            bool firstInstance;
            using (var mutex = new Mutex(true, Ipc.Prefix + "instance", out firstInstance))
            {
                if (!firstInstance)
                {
                    if (command != "background") Ipc.Send(command);
                    return 0;
                }
                if (command == "exit") return 0;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Native.DisablePowerThrottling();
                Application.Run(new TrayApp(command));
                mutex.ReleaseMutex();
            }
            return 0;
        }

        static string ParseCommand(string[] args)
        {
            foreach (string a in args)
            {
                string c = a.Trim().TrimStart('-', '/').ToLowerInvariant();
                if (c == "background" || Array.IndexOf(Ipc.Commands, c) >= 0) return c;
            }
            return "show";
        }
    }

    // Lets a second launch ("TouchpadLock.exe --toggle") control the running instance.
    static class Ipc
    {
        public const string Prefix = @"Local\TouchpadLock-2b7d91e0-";
        public static readonly string[] Commands = { "show", "toggle", "lock", "unlock", "exit" };

        public static EventWaitHandle Open(string command)
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + command);
        }

        public static void Send(string command)
        {
            using (EventWaitHandle e = Open(command)) e.Set();
        }
    }

    // Starts the app the moment you sign in, using a Task Scheduler task. No admin rights needed.
    static class Autostart
    {
        const string TaskName = "Touchpad Lock";

        public static bool IsEnabled()
        {
            return Schtasks("/query /tn \"" + TaskName + "\"") == 0;
        }

        public static void Enable()
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>Starts Touchpad Lock when you sign in.</Description></RegistrationInfo>\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
                "  <Settings>\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
                "    <Priority>4</Priority>\n" +
                "  </Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(Application.ExecutablePath) +
                "\"</Command><Arguments>--background</Arguments></Exec></Actions>\n" +
                "</Task>\n";
            string file = Path.Combine(Path.GetTempPath(), "TouchpadLock-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                if (Schtasks("/create /f /tn \"" + TaskName + "\" /xml \"" + file + "\"") != 0)
                    throw new Exception("Windows Task Scheduler could not create the startup task.");
            }
            finally { File.Delete(file); }
        }

        public static void Disable()
        {
            Schtasks("/delete /f /tn \"" + TaskName + "\"");
        }

        static int Schtasks(string arguments)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using (Process p = Process.Start(info))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        public static readonly Color UnlockedColor = Color.FromArgb(34, 160, 80);
        public static readonly Color LockedColor = Color.FromArgb(220, 53, 69);
        const string HotKeyName = "Ctrl+Alt+T";

        readonly SynchronizationContext ui;
        readonly NotifyIcon tray;
        readonly IslandForm island;
        readonly OwnedIcon unlockedIcon, lockedIcon;
        readonly ToolStripMenuItem miToggle, miStartup;
        readonly ManualResetEvent stopIpc = new ManualResetEvent(false);
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 500 };   // reading the state takes microseconds
        readonly bool hasHotKey;
        bool locked, busy, exitWhenDone, exiting, startupEnabled;

        // Exists while the app is holding the touchpad off; found at start = the app was cut short.
        static string FlagFile { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "locked.flag"); } }

        // Left behind by the first run, so "Start with Windows" is only turned on by itself once.
        static string FirstRunFile
        {
            get
            {
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TouchpadLock"), "first-run-done");
            }
        }

        public TrayApp(string startCommand)
        {
            bool firstRun = !File.Exists(FirstRunFile);
            ui = SynchronizationContext.Current;
            int iconSize = SystemInformation.SmallIconSize.Width;
            unlockedIcon = Icons.Make(UnlockedColor, false, iconSize);
            lockedIcon = Icons.Make(LockedColor, true, iconSize);

            island = new IslandForm();
            island.UnlockRequested += (s, e) => SetLocked(false);
            island.HotKeyPressed += (s, e) => SetLocked(!locked);
            hasHotKey = Native.RegisterHotKey(island.Handle, 1,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.T);

            var menu = new ContextMenuStrip();
            miToggle = new ToolStripMenuItem("Lock touchpad", null, (s, e) => SetLocked(!locked));
            miToggle.Font = new Font(miToggle.Font, FontStyle.Bold);
            miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            menu.Items.Add(miToggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miStartup);
            menu.Items.Add(new ToolStripMenuItem("Help", null, (s, e) => ShowHelp()));
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApp()));
            menu.Opening += (s, e) => miStartup.Checked = startupEnabled;

            tray = new NotifyIcon { ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetLocked(!locked); };

            Application.ThreadException += (s, e) =>
            {
                SetLocked(false);
                MessageBox.Show("Something went wrong, so the touchpad was unlocked.\n\n" + e.Exception.Message,
                    "Touchpad Lock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };

            UpdateUi();
            tray.Visible = true;
            PromoteTrayIconSoon();
            poll.Tick += (s, e) => { Refresh(); CheckWayOut(); island.KeepOnTop(); };
            poll.Start();
            StartIpcListener();
            SetUpAutostart(firstRun);

            if (startCommand == "lock" || startCommand == "toggle") SetLocked(true);
            else if (File.Exists(FlagFile)) SetLocked(false);
            else Refresh();
            if (firstRun)
                tray.ShowBalloonTip(4000, "Touchpad Lock is ready",
                    (hasHotKey ? "Press " + HotKeyName + " or click" : "Click") + " the touchpad icon to lock or unlock the touchpad. " +
                    "It starts with Windows; change that from the icon's menu.", ToolTipIcon.Info);
            else if (startCommand == "show") RunCommand("show");
        }

        void RunCommand(string command)
        {
            switch (command)
            {
                case "toggle": SetLocked(!locked); break;
                case "lock": SetLocked(true); break;
                case "unlock": SetLocked(false); break;
                case "exit": ExitApp(); break;
                case "show":
                    tray.ShowBalloonTip(4000, "Touchpad Lock is running",
                        (hasHotKey ? "Press " + HotKeyName + " or click" : "Click") + " the touchpad icon in the tray to lock or unlock the touchpad.",
                        ToolTipIcon.Info);
                    break;
            }
        }

        void SetLocked(bool wanted)
        {
            if (busy || exiting) return;
            busy = true;
            if (wanted) WriteFlag();
            Run(wanted, ok =>
            {
                busy = false;
                Refresh();
                if (exitWhenDone) ExitApp();
                else if (!ok)
                    tray.ShowBalloonTip(4000, "Touchpad Lock",
                        "Windows did not " + (wanted ? "lock" : "unlock") + " the touchpad. Click the desktop, then try again. (It needs a Windows Precision Touchpad.)",
                        ToolTipIcon.Warning);
            });
        }

        void Run(bool wanted, Action<bool> finished)
        {
            IntPtr pill = island.Handle;
            var worker = new Thread(() =>
            {
                IntPtr previous = Native.GetForegroundWindow();
                bool retried = false, ok = false;
                // Windows ignores software key presses while an administrator's window is in front,
                // so a retry briefly brings our own window to the front.
                try { ok = Touchpad.SetEnabled(!wanted, () => { retried = true; Native.SetForegroundWindow(pill); Thread.Sleep(60); }); }
                catch { }
                if (retried) Native.SetForegroundWindow(previous);
                ui.Post(_ => finished(ok), null);
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void Refresh()
        {
            if (exiting) return;
            bool now = !Touchpad.IsEnabled();
            if (!now && !busy) DeleteFlag();
            if (now == locked) return;
            locked = now;
            UpdateUi();
        }

        // Keyboard locked, touchpad locked and no mouse: nothing left to unlock with, so give it back.
        void CheckWayOut()
        {
            if (locked && IslandForm.KeyboardPillVisible() && !Touchpad.MousePresent()) SetLocked(false);
        }

        void UpdateUi()
        {
            tray.Icon = (locked ? lockedIcon : unlockedIcon).Icon;
            tray.Text = (locked ? "Touchpad Lock: LOCKED" : "Touchpad Lock: unlocked") + (hasHotKey ? "  (" + HotKeyName + ")" : "");
            miToggle.Text = locked ? "Unlock touchpad" : "Lock touchpad";
            if (locked) island.ShowLocked("Double-click to unlock" + (hasHotKey ? ", or " + HotKeyName : ""));
            else island.ShowUnlocked();
        }

        static void WriteFlag()
        {
            try { File.WriteAllText(FlagFile, ""); } catch { }
        }

        static void DeleteFlag()
        {
            try { if (File.Exists(FlagFile)) File.Delete(FlagFile); } catch { }
        }

        // Windows 11 hides new tray icons under the ^ arrow. Mark ours "show on taskbar", unless the
        // user has hidden it on purpose (IsPromoted = 0).
        void PromoteTrayIconSoon()
        {
            int attempts = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                if (!PromoteTrayIcon() && ++attempts < 6) return;
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        }

        static bool PromoteTrayIcon()
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                {
                    if (root == null) return true;
                    bool found = false;
                    foreach (string name in root.GetSubKeyNames())
                        using (RegistryKey key = root.OpenSubKey(name, true))
                        {
                            string path = key == null ? null : key.GetValue("ExecutablePath") as string;
                            if (!string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
                            found = true;
                            if (key.GetValue("IsPromoted") == null) key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        }
                    return found;
                }
            }
            catch { return true; }
        }

        void StartIpcListener()
        {
            var handles = new WaitHandle[Ipc.Commands.Length + 1];
            for (int i = 0; i < Ipc.Commands.Length; i++) handles[i] = Ipc.Open(Ipc.Commands[i]);
            handles[Ipc.Commands.Length] = stopIpc;
            var listener = new Thread(() =>
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    if (i == Ipc.Commands.Length) break;
                    string command = Ipc.Commands[i];
                    ui.Post(_ => RunCommand(command), null);
                }
            });
            listener.IsBackground = true;
            listener.Start();
        }

        // First run: turn on. Already on: refresh, so the task always points at this exe.
        void SetUpAutostart(bool firstRun)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool enabled = false;
                try
                {
                    enabled = Autostart.IsEnabled();
                    if (firstRun || enabled)
                    {
                        Autostart.Enable();
                        enabled = true;
                    }
                    if (firstRun)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(FirstRunFile));
                        File.WriteAllText(FirstRunFile, "");
                    }
                }
                catch { }
                ui.Post(__ => startupEnabled = enabled, null);
            });
        }

        void ToggleStartup()
        {
            try
            {
                if (startupEnabled) Autostart.Disable();
                else Autostart.Enable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change \"Start with Windows\".\n\n" + ex.Message, "Touchpad Lock",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            startupEnabled = Autostart.IsEnabled();
        }

        void ShowHelp()
        {
            MessageBox.Show(
                "Touchpad Lock turns the laptop touchpad off, so palms and little hands can't move the mouse.\n\n" +
                "LOCK OR UNLOCK\n" +
                "•  Click the touchpad icon in the taskbar tray" + (hasHotKey ? ", or press " + HotKeyName : "") + ".\n" +
                "•  While locked, double-click the \"Touchpad locked\" pill at the top of the screen with a mouse.\n\n" +
                "GOOD TO KNOW\n" +
                "•  Only the touchpad is blocked. A mouse keeps working.\n" +
                "•  Exiting this app turns the touchpad back on.\n" +
                "•  You can also turn it on in Windows Settings > Bluetooth & devices > Touchpad.",
                "Touchpad Lock - Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ExitApp()
        {
            if (exiting) return;
            if (busy) { exitWhenDone = true; return; }
            busy = true;
            if (locked) Run(false, ok => Quit());
            else Quit();
        }

        void Quit()
        {
            exiting = true;
            if (Touchpad.IsEnabled()) DeleteFlag();
            poll.Dispose();
            stopIpc.Set();
            Native.UnregisterHotKey(island.Handle, 1);
            tray.Visible = false;
            tray.Dispose();
            island.Dispose();
            unlockedIcon.Dispose();
            lockedIcon.Dispose();
            ExitThread();
        }
    }
}
