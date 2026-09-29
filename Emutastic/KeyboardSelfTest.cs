using Emutastic.Configuration;
using Emutastic.Services;
using Emutastic.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Emutastic
{
    /// <summary>
    /// <c>Emutastic.exe --selftest-keyboard [report.log] --portable</c>: player 1's keyboard,
    /// from the Preferences rows to what the game window presses.
    ///
    /// 1. <see cref="KeyboardBindings"/>: with nothing bound, the keys are exactly the game
    ///    window's old fixed keys; a bind moves its button off its old key; a key taken for
    ///    another button leaves its old button with none; stick rows bind; every row of every
    ///    console reaches a button or stick direction the game reads.
    /// 2. <see cref="KeyboardPad"/>: a button held by two keys, auto-repeat, opposite stick
    ///    directions, a new map letting go of held keys.
    /// 3. Preferences → Controls, the real window, keys pressed through WPF's input manager
    ///    (InputManager.ProcessInput, the path a real key takes): a key pressed while a row
    ///    waits binds it — dead until now, because the window handled PreviewKeyDown and the
    ///    KeyDown that would have read the key is never raised after that; auto-repeat doesn't
    ///    bind the next row; Alt binds as Alt; a key aimed at a focused control binds instead of
    ///    reaching it; Escape cancels; rows with no bind show the built-in key; Reset Defaults
    ///    returns to them; the keyboard lists no hotkey rows. A bare window wired the old way is
    ///    the control: the same presses must not reach its capture.
    ///
    /// Needs --portable: the theme comes from PortableData. The window's configuration is
    /// never loaded, so it cannot be saved over anything. Shows one window, off screen, and
    /// never closes it (the process exits). Exit code 0 = pass, 1 = a check failed,
    /// 2 = incomplete.
    /// </summary>
    internal static class KeyboardSelfTest
    {
        public static int Run(string? reportPath)
        {
            string path = string.IsNullOrWhiteSpace(reportPath)
                ? Path.Combine(AppContext.BaseDirectory, "keyboard-selftest.log")
                : reportPath!;
            using var r = new Report(path);
            try
            {
                // Runs on the UI thread with the dispatcher pumping, so the window loads and
                // the awaits below come back to this thread.
                var frame = new DispatcherFrame();
                Task<int> test = RunAsync(r);
                test.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                return test.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                r.Line($"  [FAIL] unhandled: {ex}");
                r.Line("=== FAIL ===");
                return 1;
            }
        }

        private static async Task<int> RunAsync(Report r)
        {
            r.Line("=== Keyboard self-test ===");
            if (!AppPaths.IsPortable)
            {
                r.Line("  [SKIP] needs --portable");
                r.Line("=== INCOMPLETE ===");
                return 2;
            }

            BindingChecks(r);
            PadChecks(r);
            await WindowChecks(r);

            if (r.Failures > 0) { r.Line($"=== FAIL ({r.Failures}) ==="); return 1; }
            if (r.Incomplete > 0) { r.Line($"=== INCOMPLETE ({r.Incomplete}) ==="); return 2; }
            r.Line("=== PASS ===");
            return 0;
        }

        // ── 1. KeyboardBindings ───────────────────────────────────────────────

        private static Dictionary<Key, uint[]> Map(string console, params (string, Key)[] binds) =>
            KeyboardBindings.ToKeyMap(KeyboardBindings.Resolve(
                console, KeyboardBindings.UsesAnalogStick(console), binds));

        private static bool Only(Dictionary<Key, uint[]> map, Key key, uint target) =>
            map.TryGetValue(key, out var t) && t.Length == 1 && t[0] == target;

        private static bool Presses(Dictionary<Key, uint[]> map, uint target) =>
            map.Values.Any(t => t.Contains(target));

        private static string Diff(Dictionary<Key, uint[]> actual, Dictionary<Key, uint> expected)
        {
            var d = new List<string>();
            foreach (var (key, target) in expected)
                if (!Only(actual, key, target))
                    d.Add($"{key}: want {target}, got {(actual.TryGetValue(key, out var t) ? string.Join("+", t) : "none")}");
            foreach (var key in actual.Keys)
                if (!expected.ContainsKey(key)) d.Add($"{key}: unexpected");
            return string.Join("; ", d);
        }

        private static void BindingChecks(Report r)
        {
            r.Line("-- KeyboardBindings --");
            // The game window's fixed keys before a bind could be saved (the switch that
            // was EmulatorWindow.SetKey's fallback), which stay the defaults.
            var digital = new Dictionary<Key, uint>
            {
                [Key.Up] = LibretroInput.JOYPAD_UP,       [Key.Down] = LibretroInput.JOYPAD_DOWN,
                [Key.Left] = LibretroInput.JOYPAD_LEFT,   [Key.Right] = LibretroInput.JOYPAD_RIGHT,
                [Key.W] = LibretroInput.JOYPAD_UP,        [Key.S] = LibretroInput.JOYPAD_DOWN,
                [Key.A] = LibretroInput.JOYPAD_LEFT,      [Key.D] = LibretroInput.JOYPAD_RIGHT,
                [Key.Z] = LibretroInput.JOYPAD_B,         [Key.X] = LibretroInput.JOYPAD_A,
                [Key.C] = LibretroInput.JOYPAD_Y,         [Key.V] = LibretroInput.JOYPAD_X,
                [Key.Q] = LibretroInput.JOYPAD_L,         [Key.E] = LibretroInput.JOYPAD_R,
                [Key.Enter] = LibretroInput.JOYPAD_START,
                [Key.LeftShift] = LibretroInput.JOYPAD_SELECT, [Key.RightShift] = LibretroInput.JOYPAD_SELECT,
                [Key.I] = LibretroInput.ANALOG_RIGHT_UP,   [Key.K] = LibretroInput.ANALOG_RIGHT_DOWN,
                [Key.J] = LibretroInput.ANALOG_RIGHT_LEFT, [Key.L] = LibretroInput.ANALOG_RIGHT_RIGHT,
            };
            var analog = new Dictionary<Key, uint>(digital)
            {
                [Key.W] = LibretroInput.ANALOG_LEFT_UP,   [Key.S] = LibretroInput.ANALOG_LEFT_DOWN,
                [Key.A] = LibretroInput.ANALOG_LEFT_LEFT, [Key.D] = LibretroInput.ANALOG_LEFT_RIGHT,
            };

            r.Check(!KeyboardBindings.UsesAnalogStick("SNES") && KeyboardBindings.UsesAnalogStick("N64"),
                "WASD is the d-pad on SNES and the left stick on N64");
            string snes = Diff(Map("SNES"), digital);
            r.Check(snes.Length == 0, $"nothing bound, SNES: the old fixed keys {snes}");
            string n64 = Diff(Map("N64"), analog);
            r.Check(n64.Length == 0, $"nothing bound, N64: the old fixed keys, WASD on the stick {n64}");
            var planted = new Dictionary<Key, uint>(digital) { [Key.C] = LibretroInput.JOYPAD_X };
            r.Check(Diff(Map("SNES"), planted).Length > 0, "control: a table with one key changed is reported different");

            var m1 = Map("SNES", ("B", Key.Space));
            r.Check(Only(m1, Key.Space, LibretroInput.JOYPAD_B) && !m1.ContainsKey(Key.Z)
                    && Only(m1, Key.X, LibretroInput.JOYPAD_A),
                "B bound to Space: Space presses B, Z (B's old key) is free, X still presses A");

            var m2 = Map("SNES", ("Start", Key.Z));
            r.Check(Only(m2, Key.Z, LibretroInput.JOYPAD_START) && !m2.ContainsKey(Key.Return)
                    && !Presses(m2, LibretroInput.JOYPAD_B),
                "Start bound to Z: Z presses only Start, Enter is free, B has no key");

            var m3 = Map("SNES", ("A", Key.K), ("B", Key.K));
            r.Check(m3.TryGetValue(Key.K, out var k3) && k3.Length == 2
                    && k3.Contains(LibretroInput.JOYPAD_A) && k3.Contains(LibretroInput.JOYPAD_B)
                    && !m3.ContainsKey(Key.X) && !m3.ContainsKey(Key.Z),
                "one key bound to two buttons presses both; their old keys are free");

            var m4 = Map("N64", ("Analog Up", Key.T), ("C Up", Key.Y));
            r.Check(Only(m4, Key.T, LibretroInput.ANALOG_LEFT_UP) && !m4.ContainsKey(Key.W)
                    && Only(m4, Key.Y, LibretroInput.ANALOG_RIGHT_UP) && !m4.ContainsKey(Key.I)
                    && Only(m4, Key.S, LibretroInput.ANALOG_LEFT_DOWN),
                "N64 stick rows bind: T is Analog Up, Y is C Up, W and I retire, S still pulls down");

            var saved = new List<ButtonMapping>
            {
                new() { ButtonName = "Up",        InputIdentifier = "W",                 InputType = Configuration.InputType.Keyboard },
                new() { ButtonName = "Disk Swap", InputIdentifier = "Return+RightShift", InputType = Configuration.InputType.Keyboard },
                new() { ButtonName = "Hotkey",    InputIdentifier = "F1",                InputType = Configuration.InputType.Keyboard },
                new() { ButtonName = "A",         InputIdentifier = "8",                 InputType = Configuration.InputType.Keyboard },
            };
            var binds = KeyboardBindings.SavedBinds(saved).ToList();
            r.Check(binds.Count == 2 && binds[0] == ("Up", Key.W) && binds[1] == ("Hotkey", Key.F1),
                "saved list: key names are binds; the Disk Swap chord and a stray number are not");
            var m5 = Map("SNES", binds.ToArray());
            r.Check(Only(m5, Key.W, LibretroInput.JOYPAD_UP) && !m5.ContainsKey(Key.F1)
                    && !m5.ContainsKey(Key.Up),
                "a bind on the Hotkey row reaches nothing, so F1 stays free; Up moved to W");

            // Rows whose binds reach nothing, known and listed with the reason; any other
            // row that reaches nothing is a definition/translator drift and fails.
            var known = new Dictionary<string, string>
            {
                ["PS3"]       = "RPCS3 reads its own pad config (Rpcs3Runtime), not these binds",
                ["CDi:Analog"] = "analog left out on purpose: the core thresholds the stick itself",
                ["3DO:Left Analog"] = "the 3DO pad is digital; the translator has no stick",
                ["Jaguar:1"] = "keypad digit not mapped yet", ["Jaguar:2"] = "keypad digit not mapped yet",
                ["Jaguar:3"] = "keypad digit not mapped yet",
                ["PSP:Home"] = "not mapped yet",
            };
            string? Known(string console, string name) =>
                known.TryGetValue(console, out var why) || known.TryGetValue($"{console}:{name}", out why)
                || known.TryGetValue($"{console}:{string.Join(' ', name.Split(' ').SkipLast(1))}", out why)
                    ? why : null;
            var dead = new List<string>();
            foreach (var (console, def) in ControllerDefinitions.AllControllers)
                foreach (var b in def.Buttons)
                {
                    if (KeyboardBindings.IsTarget(LibretroInput.GetButtonId(b.Name, console))) continue;
                    if (Known(console, b.Name) is string why) r.Line($"  [INFO] {console}:{b.Name} reaches nothing — {why}");
                    else dead.Add($"{console}:{b.Name}");
                }
            r.Check(dead.Count == 0,
                "every other row of every console reaches a button or stick direction"
                + (dead.Count > 0 ? $" — these reach nothing: {string.Join(", ", dead)}" : ""));
        }

        // ── 2. KeyboardPad ────────────────────────────────────────────────────

        private static void PadChecks(Report r)
        {
            r.Line("-- KeyboardPad --");
            var buttons = new bool[16];
            var pad = new KeyboardPad(buttons);

            pad.SetMap(Map("SNES"));
            pad.Set(Key.W, true); pad.Set(Key.Up, true); pad.Set(Key.W, false);
            bool held = buttons[LibretroInput.JOYPAD_UP];
            pad.Set(Key.Up, false);
            r.Check(held && !buttons[LibretroInput.JOYPAD_UP],
                "Up held by W and the arrow stays down until the last of the two lets go");

            pad.Set(Key.Z, true); pad.Set(Key.Z, true); pad.Set(Key.Z, false);
            r.Check(!buttons[LibretroInput.JOYPAD_B], "auto-repeat downs are one press: one release lets go");

            pad.SetMap(Map("N64"));
            pad.Set(Key.W, true);
            short up = pad.LeftY;
            pad.Set(Key.S, true);
            short both = pad.LeftY;
            pad.Set(Key.W, false);
            short down = pad.LeftY;
            pad.Set(Key.S, false);
            r.Check(up == -32767 && both == 0 && down == 32767 && pad.LeftY == 0,
                $"N64 stick: W up (-32767), W+S cancel (0), S alone down (+32767) — got {up}/{both}/{down}/{pad.LeftY}");

            pad.Set(Key.X, true);
            pad.SetMap(Map("N64"));
            pad.Set(Key.X, false);
            r.Check(buttons.All(b => !b), "a new map lets go of every key held");
        }

        // ── 3. Preferences → Controls ─────────────────────────────────────────

        private static async Task WindowChecks(Report r)
        {
            r.Line("-- Preferences > Controls --");
            try
            {
                ThemeService.Instance.ScanInstalledThemes();
                ThemeService.Instance.LoadAndApplyTheme(new ThemeConfiguration().ActiveThemeId);
            }
            catch (Exception ex) { r.Line($"  (theme: {ex.Message})"); }

            OldWiringControl(r);

            var config = new JsonConfigurationService(null);   // never loaded, so never saved
            var win = new PreferencesWindow(null!, null!, config, null, "SNES")
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
            };
            win.Show();
            if (!await Until(() => win.IsLoaded && Rows(win).ContainsKey("Up")
                                   && win.FindName("InputDeviceComboBox") is ComboBox { SelectedItem: "Keyboard" }))
            {
                r.Line("  [SKIP] the window never loaded SNES on the keyboard");
                r.Incomplete++;
                return;
            }

            var rows = Rows(win);
            string Text(string row) => Rows(win).TryGetValue(row, out var x) ? x.Label.Text : "(no row)";

            r.Check(!rows.ContainsKey("Hotkey") && !rows.ContainsKey("Save State") && !rows.ContainsKey("Load State"),
                "the keyboard lists no Hotkey / Save State / Load State rows");
            string builtIn = $"Up={Text("Up")} B={Text("B")} A={Text("A")} Y={Text("Y")} X={Text("X")} "
                           + $"L={Text("L")} R={Text("R")} Select={Text("Select")} Start={Text("Start")}";
            r.Check(Text("Up") == "↑" && Text("B") == "Z" && Text("A") == "X" && Text("Y") == "C"
                    && Text("X") == "V" && Text("L") == "Q" && Text("R") == "E"
                    && Text("Select") == "R Shift" && Text("Start") == "Enter",
                $"nothing bound: each row shows the key the game uses ({builtIn})");

            Click(rows["Up"].Box);
            await Pump();
            r.Check(Text("Up") == "Press a button…", "clicking Up waits for a key");

            Press(win, Key.W);
            await Pump();
            r.Check(Text("Up") == "W" && Text("Down") == "Press a button…",
                $"pressing W binds Up and moves to Down (Up={Text("Up")}, Down={Text("Down")})");

            if (Press(win, Key.W, repeat: true))
            {
                await Pump();
                r.Check(Text("Down") == "Press a button…",
                    $"auto-repeat of W doesn't bind Down too (Down={Text("Down")})");
            }
            else { r.Line("  [SKIP] KeyEventArgs.SetRepeat not found — repeat not tested"); r.Incomplete++; }

            if (Press(win, Key.LeftAlt, system: true))
            {
                await Pump();
                r.Check(Text("Down") == "L Alt" && Text("Left") == "Press a button…",
                    $"Alt (which WPF reports as Key.System) binds as L Alt (Down={Text("Down")})");
            }
            else { r.Line("  [SKIP] KeyEventArgs.MarkSystem not found — Alt not tested"); r.Incomplete++; }

            if (win.FindName("SystemComboBox") is ComboBox systems)
            {
                int before = systems.SelectedIndex;
                Press(win, Key.Down, target: systems);
                await Pump();
                r.Check(Text("Left") == "↓" && systems.SelectedIndex == before,
                    $"a key aimed at the focused console list binds Left and doesn't change the console (Left={Text("Left")}, index {before}→{systems.SelectedIndex})");
            }
            else { r.Line("  [SKIP] SystemComboBox not found"); r.Incomplete++; }

            Press(win, Key.Escape);
            await Pump();
            r.Check(Text("Right") == "→", $"Escape stops waiting; Right keeps its built-in key (Right={Text("Right")})");

            Click(Rows(win)["Start"].Box);
            Press(win, Key.Z);
            await Pump();
            r.Check(Text("Start") == "Z" && Text("B") == "—",
                $"binding Start to Z leaves B with no key (Start={Text("Start")}, B={Text("B")})");

            if (Rows(win).Values.Any(x => x.Label.Text == "Press a button…")) Press(win, Key.Escape);
            if (Find<Button>(win, b => Equals(b.Content, "Reset Defaults")) is Button reset)
            {
                reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Pump();
                r.Check(Text("Up") == "↑" && Text("Start") == "Enter" && Text("B") == "Z" && Text("Down") == "↓",
                    $"Reset Defaults returns every row to its built-in key (Up={Text("Up")}, Start={Text("Start")}, B={Text("B")})");
            }
            else { r.Line("  [SKIP] Reset Defaults button not found"); r.Incomplete++; }
        }

        /// <summary>
        /// The reported fault, reproduced: a bare window wired as Preferences was (PreviewKeyDown
        /// marks the key handled while a row waits, KeyDown reads it). The presses the real window
        /// gets below must not reach this capture. Then the same window without the handled
        /// PreviewKeyDown — the key does reach KeyDown when this session can deliver it, which
        /// shows it is the handled PreviewKeyDown, not the harness, that starves the capture.
        /// </summary>
        private static void OldWiringControl(Report r)
        {
            bool markHandled = true, captured = false;
            var probe = new Window
            {
                Width = 200, Height = 100, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            probe.PreviewKeyDown += (_, e) => { if (markHandled) e.Handled = true; };
            probe.KeyDown += (_, e) => captured = true;
            probe.Show();
            probe.Activate();
            Keyboard.Focus(probe);

            Press(probe, Key.W);
            r.Check(!captured, "control: wired the old way, a pressed key never reaches the capture");

            markHandled = false; captured = false;
            Press(probe, Key.W);
            r.Line(captured
                ? "  [INFO] without the handled PreviewKeyDown the same press reaches KeyDown — the handled preview is the cause"
                : "  [INFO] this session gives the probe no keyboard focus, so KeyDown can't be delivered at all; the control above is then not specific");
            probe.Close();
        }

        // ── WPF plumbing ──────────────────────────────────────────────────────

        private static readonly MethodInfo? SetRepeat =
            typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo? MarkSystem =
            typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>A key-down through the input manager, as the keyboard delivers one: the
        /// tunnelling PreviewKeyDown, promoted to KeyDown if nothing handled it. Returns false
        /// when a requested flag can't be set on this WPF build.</summary>
        private static bool Press(Window win, Key key, IInputElement? target = null,
            bool repeat = false, bool system = false)
        {
            if ((repeat && SetRepeat == null) || (system && MarkSystem == null)) return false;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(win),
                Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = target ?? win,
            };
            if (repeat) SetRepeat!.Invoke(args, new object[] { true });
            if (system) MarkSystem!.Invoke(args, null);
            InputManager.Current.ProcessInput(args);
            return true;
        }

        private static void Click(Border box) =>
            box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = box,
            });

        /// <summary>Mapping rows by button name: the Border that is clicked and its label.</summary>
        private static Dictionary<string, (Border Box, TextBlock Label)> Rows(Window win)
        {
            var rows = new Dictionary<string, (Border, TextBlock)>();
            if (win.FindName("ButtonsPanel") is not StackPanel panel) return rows;
            foreach (var child in panel.Children)
                if (child is Grid g)
                    foreach (var c in g.Children)
                        if (c is Border { Tag: string name, Child: TextBlock label } box)
                            rows[name] = (box, label);
            return rows;
        }

        private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t && match(t)) return t;
                if (Find(child, match) is T found) return found;
            }
            return null;
        }

        private static Task Pump() =>
            Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

        private static async Task<bool> Until(Func<bool> done, int timeoutMs = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (done()) return true;
                await Task.Delay(50);
            }
            return done();
        }

        private sealed class Report : IDisposable
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AttachConsole(uint dwProcessId);
            private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

            private readonly StreamWriter? _file;
            private readonly StreamWriter? _console;
            public int Failures, Incomplete;

            public Report(string path)
            {
                try
                {
                    if (AttachConsole(ATTACH_PARENT_PROCESS))
                        _console = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                }
                catch { }
                try
                {
                    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    _file = new StreamWriter(path, append: false) { AutoFlush = true };
                }
                catch { }
            }

            public void Line(string text)
            {
                try { _console?.WriteLine(text); } catch { }
                try { _file?.WriteLine(text); } catch { }
            }

            public void Check(bool ok, string what)
            {
                Line($"  [{(ok ? "PASS" : "FAIL")}] {what}");
                if (!ok) Failures++;
            }

            public void Dispose()
            {
                try { _file?.Dispose(); } catch { }
                try { _console?.Dispose(); } catch { }
            }
        }
    }
}
