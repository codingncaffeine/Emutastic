using Emutastic.Configuration;
using Emutastic.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Emutastic.Views
{
    // ── Sidebar layout editor (Preferences → Library), ported from the Linux build. Rows come
    //    from ConsoleCatalog rather than fixed markup, so the list stays right as consoles come
    //    and go. Applies LIVE: each change writes LibraryConfiguration, saves, and rebuilds the
    //    sidebar in the main window — a layout setting that only took effect after a restart
    //    would read as broken. ──
    public partial class PreferencesWindow
    {
        // Populating rows assigns IsChecked, which raises Checked/Unchecked exactly as a user
        // click does. Without this guard the handlers write config and rebuild the panel they
        // are being built from.
        private bool _populatingSidebarLayout;

        private LibraryConfiguration SidebarLib
        {
            get
            {
                var lib = _configService.GetLibraryConfiguration();
                // A hand-edited config.json can carry null for any of these.
                lib.ConsoleOrder ??= new();
                lib.GroupOrder ??= new();
                lib.HiddenConsoles ??= new();
                lib.HiddenGroups ??= new();
                return lib;
            }
        }

        /// <summary>The main window, whose sidebar these settings drive.</summary>
        private Emutastic.MainWindow? LibraryWindow
            => Owner as Emutastic.MainWindow ?? Application.Current?.MainWindow as Emutastic.MainWindow;

        private void LoadSidebarLayout()
        {
            _populatingSidebarLayout = true;
            try { HideEmptyConsolesCheck.IsChecked = SidebarLib.HideEmptyConsoles; }
            finally { _populatingSidebarLayout = false; }
            BuildSidebarLayoutEditor();
        }

        private void HideEmptyConsoles_Changed(object sender, RoutedEventArgs e)
        {
            if (_populatingSidebarLayout) return;
            SidebarLib.HideEmptyConsoles = HideEmptyConsolesCheck.IsChecked == true;
            CommitSidebarLayout(rebuildEditor: true, status: "");
        }

        private void RestoreSidebarDefaults_Click(object sender, RoutedEventArgs e)
        {
            var lib = SidebarLib;
            // CLEAR rather than write a copy of the defaults: empty means "use the built-in
            // layout", so the user keeps receiving consoles added by future updates.
            lib.ConsoleOrder.Clear();
            lib.GroupOrder.Clear();
            lib.HiddenConsoles.Clear();
            lib.HiddenGroups.Clear();
            lib.HideEmptyConsoles = false;

            _populatingSidebarLayout = true;
            try { HideEmptyConsolesCheck.IsChecked = false; }
            finally { _populatingSidebarLayout = false; }

            CommitSidebarLayout(rebuildEditor: true, status: "Sidebar restored to its default layout.");
        }

        private void BuildSidebarLayoutEditor()
        {
            var host = SidebarLayoutPanel;
            host.Children.Clear();
            var lib = SidebarLib;
            var counts = lib.HideEmptyConsoles ? LibraryWindow?.GameCountsByConsole() : null;

            bool previous = _populatingSidebarLayout;
            _populatingSidebarLayout = true;
            try
            {
                // Say what the controls do, in the panel — a button you have to click to find
                // out about is the same as no button at all.
                var legend = new TextBlock
                {
                    Text = "Untick a console to hide it. Up / Down reorder within a group; on a "
                         + "manufacturer heading they move that whole group and its consoles together.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                legend.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                host.Children.Add(legend);

                // Positions are materialised so each row knows whether it can actually move. A
                // button that is enabled but does nothing when clicked reads as a broken feature.
                var ungrouped = ConsoleCatalog.InUserOrder(ConsoleCatalog.Ungrouped, lib.ConsoleOrder, e => e.Tag).ToList();
                for (int u = 0; u < ungrouped.Count; u++)
                    host.Children.Add(ConsoleRow(ungrouped[u], lib, counts, indent: 0,
                                                 first: u == 0, last: u == ungrouped.Count - 1));

                var groups = ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, g => g).ToList();
                for (int i = 0; i < groups.Count; i++)
                {
                    host.Children.Add(GroupRow(groups[i], lib, first: i == 0, last: i == groups.Count - 1));
                    var inGroup = ConsoleCatalog.InUserOrder(ConsoleCatalog.InGroup(groups[i]), lib.ConsoleOrder, e => e.Tag).ToList();
                    for (int c = 0; c < inGroup.Count; c++)
                        host.Children.Add(ConsoleRow(inGroup[c], lib, counts, indent: 18,
                                                     first: c == 0, last: c == inGroup.Count - 1));
                }
            }
            finally { _populatingSidebarLayout = previous; }

            DumpSidebarLayoutDiag(host);
        }

        // ── Rows ────────────────────────────────────────────────────────────────────────────
        private static Grid ThreeColumnRow(Thickness margin)
        {
            var row = new Grid { Margin = margin };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            return row;
        }

        private Grid GroupRow(string group, LibraryConfiguration lib, bool first, bool last)
        {
            bool hiddenGroup = lib.HiddenGroups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));

            var check = new CheckBox
            {
                IsChecked = !hiddenGroup,
                Content = group,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            RoutedEventHandler toggled = (_, _) =>
            {
                if (_populatingSidebarLayout) return;
                SetHidden(SidebarLib.HiddenGroups, group, hidden: check.IsChecked != true);
                // Rebuild so the consoles beneath show that their heading is hidden.
                CommitSidebarLayout(rebuildEditor: true, status: "");
            };
            check.Checked += toggled;
            check.Unchecked += toggled;

            var row = ThreeColumnRow(new Thickness(0, 12, 0, 2));
            Grid.SetColumn(check, 0);
            row.Children.Add(check);
            row.Children.Add(MoveButton("Up", 1, !first, $"Move the whole {group} group, and its consoles, up",
                                        () => MoveGroup(group, -1)));
            row.Children.Add(MoveButton("Down", 2, !last, $"Move the whole {group} group, and its consoles, down",
                                        () => MoveGroup(group, +1)));
            return row;
        }

        private Grid ConsoleRow(ConsoleCatalogEntry entry, LibraryConfiguration lib,
                                Dictionary<string, int>? counts, int indent, bool first, bool last)
        {
            bool hidden = lib.HiddenConsoles.Any(c => string.Equals(c, entry.Tag, StringComparison.OrdinalIgnoreCase));
            bool groupHidden = entry.Group != null
                && lib.HiddenGroups.Any(g => string.Equals(g, entry.Group, StringComparison.OrdinalIgnoreCase));
            bool empty = counts != null && counts.GetValueOrDefault(entry.Tag) == 0;
            // Dim rows that are off for a reason other than their own checkbox, so it is obvious
            // why they are not in the sidebar. The checkbox itself stays clear: it still works.
            bool dimmed = groupHidden || empty;

            string suffix = groupHidden ? "  (group hidden)" : empty ? "  (no games)" : "";
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            if (ConsoleCatalog.IconUri(entry.Tag) is string iconUri)
            {
                var icon = new Image
                {
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 6, 0),
                    Source = LayoutIcon(iconUri),
                    Opacity = dimmed ? 0.45 : 1.0,
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                label.Children.Add(icon);
            }
            label.Children.Add(new TextBlock { Text = entry.DisplayName + suffix, VerticalAlignment = VerticalAlignment.Center });

            var check = new CheckBox
            {
                IsChecked = !hidden,
                Content = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(ForegroundProperty, dimmed ? "TextMutedBrush" : "TextPrimaryBrush");
            RoutedEventHandler toggled = (_, _) =>
            {
                if (_populatingSidebarLayout) return;
                SetHidden(SidebarLib.HiddenConsoles, entry.Tag, hidden: check.IsChecked != true);
                CommitSidebarLayout(rebuildEditor: false, status: "");
            };
            check.Checked += toggled;
            check.Unchecked += toggled;

            string where = entry.Group == null ? "the top of the list" : $"the {entry.Group} group";
            var row = ThreeColumnRow(new Thickness(indent, 1, 0, 1));
            Grid.SetColumn(check, 0);
            row.Children.Add(check);
            row.Children.Add(MoveButton("Up", 1, !first, $"Move {entry.DisplayName} up within {where}",
                                        () => MoveConsole(entry, -1)));
            row.Children.Add(MoveButton("Down", 2, !last, $"Move {entry.DisplayName} down within {where}",
                                        () => MoveConsole(entry, +1)));
            return row;
        }

        private readonly Dictionary<string, ImageSource?> _layoutIcons = new(StringComparer.Ordinal);

        private ImageSource? LayoutIcon(string uri)
        {
            if (_layoutIcons.TryGetValue(uri, out var cached)) return cached;
            ImageSource? source = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(uri, UriKind.Absolute);
                bmp.DecodePixelWidth = 48;   // shown at 16 px; plenty for high-DPI screens
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                source = bmp;
            }
            catch { /* a missing icon just leaves the row text-only */ }
            _layoutIcons[uri] = source;
            return source;
        }

        /// <summary>
        /// A small labelled move button. No fixed Width: the label sizes it, and a shared
        /// minimum keeps the Up and Down columns aligned. SecondaryBtn has no disabled look,
        /// so a button that cannot act is faded here.
        /// </summary>
        private Button MoveButton(string label, int column, bool enabled, string tip, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                Style = (Style)FindResource("SecondaryBtn"),
                FontSize = 11,
                Padding = new Thickness(10, 3, 10, 3),
                MinWidth = 54,
                IsEnabled = enabled,
                Opacity = enabled ? 1.0 : 0.35,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tip,
                Tag = "move",
            };
            btn.Click += (_, _) => { if (!_populatingSidebarLayout) onClick(); };
            Grid.SetColumn(btn, column);
            return btn;
        }

        // ── Mutations ───────────────────────────────────────────────────────────────────────
        private static void SetHidden(List<string> list, string value, bool hidden)
        {
            bool present = list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
            if (hidden && !present) list.Add(value);
            else if (!hidden && present) list.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Moves a console within its own group. The full effective order is written back, so
        /// the stored list is unambiguous however little the user had reordered before.
        /// </summary>
        private void MoveConsole(ConsoleCatalogEntry entry, int delta)
        {
            var lib = SidebarLib;
            var siblings = SidebarSequenceFor(entry.Group, lib).ToList();
            int i = siblings.IndexOf(entry.Tag);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= siblings.Count) return;
            (siblings[i], siblings[j]) = (siblings[j], siblings[i]);

            var full = new List<string>();
            full.AddRange(entry.Group == null ? siblings : SidebarSequenceFor(null, lib));
            foreach (string g in ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, x => x))
                full.AddRange(string.Equals(g, entry.Group, StringComparison.Ordinal) ? siblings : SidebarSequenceFor(g, lib));

            lib.ConsoleOrder = full;
            CommitSidebarLayout(rebuildEditor: true, status: "");
        }

        private void MoveGroup(string group, int delta)
        {
            var lib = SidebarLib;
            var order = ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, lib.GroupOrder, g => g).ToList();
            int i = order.IndexOf(group);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]);

            lib.GroupOrder = order;
            CommitSidebarLayout(rebuildEditor: true, status: "");
        }

        private static IEnumerable<string> SidebarSequenceFor(string? group, LibraryConfiguration lib)
            => ConsoleCatalog.InUserOrder(
                group == null ? ConsoleCatalog.Ungrouped : ConsoleCatalog.InGroup(group),
                lib.ConsoleOrder, e => e.Tag).Select(e => e.Tag);

        private void CommitSidebarLayout(bool rebuildEditor, string status)
        {
            _configService.SetLibraryConfiguration(SidebarLib);
            _ = _configService.SaveAsync();
            LibraryWindow?.BuildConsoleSidebar();          // live — never wait for a restart
            if (rebuildEditor) BuildSidebarLayoutEditor();
            SidebarLayoutStatusText.Text = status;
        }

        /// <summary>
        /// With EMUTASTIC_SIDEBAR_DIAG=1, records the editor as built — row count, whether the
        /// main window resolved (if not, every change saves config and updates nothing) and,
        /// after a layout pass, the rendered size of the move buttons.
        /// </summary>
        private void DumpSidebarLayoutDiag(StackPanel host)
        {
            if (Environment.GetEnvironmentVariable("EMUTASTIC_SIDEBAR_DIAG") != "1") return;
            try
            {
                string logPath = System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log");
                System.IO.File.AppendAllText(logPath,
                    $"=== layout editor built {DateTime.Now:HH:mm:ss.fff} === rows={host.Children.Count}"
                    + $" mainWindow={(LibraryWindow != null ? "resolved" : "NULL - live rebuild would silently do nothing")}\n");

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var buttons = host.Children.OfType<Grid>()
                            .SelectMany(g => g.Children.OfType<Button>())
                            .Where(b => (b.Tag as string) == "move")
                            .ToList();
                        int narrow = buttons.Count(b => b.ActualWidth < 32);
                        int disabled = buttons.Count(b => !b.IsEnabled);
                        var sample = buttons.Take(2).Select(b => $"'{b.Content}' {b.ActualWidth:F0}x{b.ActualHeight:F0}");
                        System.IO.File.AppendAllText(logPath,
                            $"    move buttons={buttons.Count} narrow(<32px)={narrow} disabled={disabled} "
                            + $"sample: {string.Join(", ", sample)} [Restore Default Layout w={RestoreSidebarDefaultsBtn.ActualWidth:F0}]\n");
                    }
                    catch { /* never throw from diagnostics */ }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { /* never throw from diagnostics */ }
        }
    }
}
