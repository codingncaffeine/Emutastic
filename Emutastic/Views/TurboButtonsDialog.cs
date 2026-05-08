using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Emutastic.Views
{
    public sealed class TurboButtonsDialog : Window
    {
        // Buttons that are never turbo-able regardless of what descriptors say.
        // Matches TurboBlacklist in EmulatorWindow.xaml.cs.
        private static readonly HashSet<uint> Blacklist = new()
        {
            2,  // SELECT
            3,  // START
            4, 5, 6, 7,    // d-pad
            14, 15,        // L3 / R3
        };

        private readonly HashSet<uint>[] _turbo;
        private readonly EmulatorWindow _owner;

        public TurboButtonsDialog(EmulatorWindow ownerWindow, HashSet<uint>[] turboButtons)
        {
            _owner = ownerWindow;
            _turbo = turboButtons;

            Title = "Turbo Buttons";
            Owner = ownerWindow;
            Width = 360;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;

            var bgBrush  = (Brush)(TryFindResource("BgSecondaryBrush")    ?? new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1C)));
            var bdBrush  = (Brush)(TryFindResource("BorderNormalBrush")   ?? new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)));
            var txtPri   = (Brush)(TryFindResource("TextPrimaryBrush")    ?? Brushes.White);
            var txtSec   = (Brush)(TryFindResource("TextSecondaryBrush")  ?? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

            var shell = new Border
            {
                Background = bgBrush,
                BorderBrush = bdBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
            };
            var root = new StackPanel { Margin = new Thickness(20) };
            // Wrap the StackPanel in a scroller so 4-port cores (NeoGeo/Saturn/GC)
            // don't produce a dialog taller than the screen.
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 600,
                Content = root,
            };
            shell.Child = scroller;
            Content = shell;

            root.Children.Add(new TextBlock
            {
                Text = "Turbo Buttons",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = txtPri,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = "Toggle to autofire (~10Hz). Per game.",
                FontSize = 11,
                Foreground = txtSec,
                Margin = new Thickness(0, 0, 0, 14),
            });

            // Determine port count from how many ports have any turboable buttons.
            // Most consoles use port 0; a few expose 2+. We render only ports with content.
            var portRows = new List<UIElement>();
            for (int port = 0; port < 4; port++)
            {
                var raw = _owner.GetTurboableButtonsForPort(port);
                var entries = raw
                    .Where(kv => !Blacklist.Contains(kv.Key))
                    // Some cores (e.g. FCEUmm/Nestopia for NES) expose extra ids labeled
                    // "Turbo A" / "Turbo B" — those are core-side autofire aliases that
                    // already turbo internally; offering a frontend turbo on top of them
                    // is meaningless. Filter by label.
                    .Where(kv => !kv.Value.TrimStart().StartsWith("Turbo ", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Key)
                    .ToList();
                if (entries.Count == 0) continue;

                if (portRows.Count > 0)
                    portRows.Add(new Rectangle
                    {
                        Height = 1, Fill = bdBrush, Margin = new Thickness(0, 6, 0, 6),
                    });

                // Show per-port heading only if more than one port will appear.
                portRows.Add(new TextBlock
                {
                    Text = $"Player {port + 1}",
                    Foreground = txtSec,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4),
                    Tag = "port-header",
                });

                int capPort = port;
                foreach (var (id, label) in entries)
                {
                    portRows.Add(BuildToggleRow(capPort, id, label, txtPri));
                }
            }

            // Hide the "Player 1" heading when only port 0 has buttons (the common case).
            int headerCount = portRows.Count(e => (e as TextBlock)?.Tag as string == "port-header");
            if (headerCount == 1)
            {
                var hdr = portRows.First(e => (e as TextBlock)?.Tag as string == "port-header");
                ((TextBlock)hdr).Visibility = Visibility.Collapsed;
            }

            if (portRows.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "No turbo-eligible buttons for this core.",
                    Foreground = txtSec,
                    FontStyle = FontStyles.Italic,
                });
            }
            else
            {
                foreach (var row in portRows) root.Children.Add(row);
            }

            // Done button (single — toggles persist on click via the swap below)
            var doneRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var done = new Button { Content = "Done", IsDefault = true };
            if (TryFindResource("PrimaryButtonStyle") is Style primaryStyle)
                done.Style = primaryStyle;
            done.Click += (_, _) => { DialogResult = true; Close(); };
            doneRow.Children.Add(done);
            root.Children.Add(doneRow);

            // Esc closes
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = true; Close(); } };
        }

        private FrameworkElement BuildToggleRow(int port, uint id, string label, Brush textBrush)
        {
            bool initial = _turbo[port].Contains(id);

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accent = (Brush)(TryFindResource("AccentBrush")
                ?? new SolidColorBrush(Color.FromRgb(0xE0, 0x35, 0x35)));
            var off    = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            var bdr    = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

            var knob = new Border
            {
                Width = 14, Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.White,
                Margin = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = initial ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var toggle = new Border
            {
                Background = initial ? accent : off,
                BorderBrush = bdr,
                BorderThickness = new Thickness(1),
                Width = 34, Height = 18,
                CornerRadius = new CornerRadius(9),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = knob,
                ToolTip = initial ? "Click to disable turbo" : "Click to enable turbo",
            };

            var text = new TextBlock
            {
                Text = label,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            void Apply(bool on)
            {
                // Atomic-replacement pattern: build a fresh set per port mutation so
                // the EmuThread sees a tear-free view (TurboGate snapshots the ref).
                var fresh = new HashSet<uint>(_turbo[port]);
                if (on) fresh.Add(id); else fresh.Remove(id);
                _turbo[port] = fresh;

                toggle.Background = on ? accent : off;
                knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                toggle.ToolTip = on ? "Click to disable turbo" : "Click to enable turbo";

                _owner.SaveTurboConfigPublic();
            }

            toggle.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                bool nowOn = _turbo[port].Contains(id);
                Apply(!nowOn);
            };
            // Clicking the row label also toggles, like cheats UX.
            grid.MouseLeftButtonDown += (_, _) =>
            {
                bool nowOn = _turbo[port].Contains(id);
                Apply(!nowOn);
            };
            grid.Cursor = Cursors.Hand;

            Grid.SetColumn(toggle, 0);
            Grid.SetColumn(text, 1);
            grid.Children.Add(toggle);
            grid.Children.Add(text);
            return grid;
        }
    }
}
