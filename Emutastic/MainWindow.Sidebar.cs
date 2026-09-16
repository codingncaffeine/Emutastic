using Emutastic.Configuration;
using Emutastic.Services;
using Emutastic.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Emutastic
{
    /// <summary>
    /// Builds the sidebar's console list from <see cref="ConsoleCatalog"/> instead of the
    /// hand-written markup that used to spell out every button (port of the Linux build's
    /// MainWindow.Sidebar.cs).
    ///
    /// ⛔ The DEFAULT MUST RENDER LIKE THE MARKUP IT REPLACED — same entries, same order, same
    /// labels, same collapsible headings, same margins, same 20×20 icons (plus the Windows row).
    /// With an untouched <see cref="LibraryConfiguration"/> every list is empty, so the output
    /// is the old sidebar. Customisation only ever removes or reorders rows; it never restyles
    /// them.
    /// </summary>
    public partial class MainWindow
    {
        // Headings the user folded this session. Survives a rebuild (changing the layout in
        // Preferences must not re-open what the user just folded); not persisted, as before.
        private readonly HashSet<string> _collapsedSidebarGroups = new(StringComparer.Ordinal);

        // One decoded bitmap per icon file, reused by every rebuild.
        private static readonly Dictionary<string, ImageSource?> _sidebarIcons = new(StringComparer.Ordinal);

        /// <summary>
        /// Tag of a heading button. Deliberately not a string: the sidebar's right-click handler
        /// reads a string Tag as a console tag.
        /// </summary>
        private sealed record SidebarGroupTag(string Group, StackPanel Section, TextBlock Arrow);

        /// <summary>
        /// Fills ConsoleNavPanel. Safe to call again after the user changes the layout in
        /// Preferences or the library changes — it clears and rebuilds, then moves the current
        /// selection's highlight (which lives on the button) onto the new button.
        /// </summary>
        public void BuildConsoleSidebar()
        {
            var panel = ConsoleNavPanel;
            if (panel == null) return;

            string? selectedTag = _selectedNavButton?.CommandParameter as string;
            panel.Children.Clear();

            var itemStyle  = (Style)FindResource("SidebarItemStyle");
            var labelStyle = (Style)FindResource("LabelMuted");

            var cfg = App.Configuration?.GetLibraryConfiguration() ?? new LibraryConfiguration();
            var hiddenConsoles = new HashSet<string>(cfg.HiddenConsoles ?? new(), StringComparer.OrdinalIgnoreCase);
            var hiddenGroups   = new HashSet<string>(cfg.HiddenGroups ?? new(), StringComparer.OrdinalIgnoreCase);

            // Only consult the database when the option is on, so the default path never pays
            // for a feature nobody enabled.
            Dictionary<string, int>? counts = null;
            if (cfg.HideEmptyConsoles && _db != null)
            {
                try { counts = _db.GetGameCountsByConsole(); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Sidebar] game counts: {ex.Message}"); }
            }

            bool Visible(ConsoleCatalogEntry e)
            {
                if (hiddenConsoles.Contains(e.Tag)) return false;
                if (e.Group != null && hiddenGroups.Contains(e.Group)) return false;
                if (counts != null && counts.GetValueOrDefault(e.Tag) == 0) return false;
                return true;
            }

            // Standalone entries (Arcade, Windows) sit above the manufacturer groups.
            foreach (var entry in ConsoleCatalog.InUserOrder(ConsoleCatalog.Ungrouped.Where(Visible), cfg.ConsoleOrder, e => e.Tag))
                panel.Children.Add(MakeConsoleButton(entry, itemStyle));

            foreach (string group in ConsoleCatalog.InUserOrder(ConsoleCatalog.DefaultGroupOrder, cfg.GroupOrder, g => g))
            {
                if (hiddenGroups.Contains(group)) continue;

                var entries = ConsoleCatalog.InUserOrder(ConsoleCatalog.InGroup(group).Where(Visible), cfg.ConsoleOrder, e => e.Tag).ToList();
                if (entries.Count == 0) continue;   // never leave a heading with nothing under it

                bool collapsed = _collapsedSidebarGroups.Contains(group);
                var section = new StackPanel { Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
                foreach (var entry in entries)
                    section.Children.Add(MakeConsoleButton(entry, itemStyle));

                var arrow = new TextBlock
                {
                    Text = collapsed ? "▸" : "▾",
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

                var headerContent = new StackPanel { Orientation = Orientation.Horizontal };
                headerContent.Children.Add(arrow);
                headerContent.Children.Add(new TextBlock
                {
                    Text = group,
                    Style = labelStyle,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var header = new Button
                {
                    Style = itemStyle,
                    Margin = new Thickness(6, 4, 6, 0),
                    Content = headerContent,
                    Tag = new SidebarGroupTag(group, section, arrow),
                };
                header.Click += SidebarGroupHeader_Click;

                panel.Children.Add(header);
                panel.Children.Add(section);
            }

            // A rebuild replaced the buttons, so move the highlight and the game-count badge
            // to the selected console's new button (if it is still listed).
            if (selectedTag != null)
            {
                if (FindSidebarButton(selectedTag) is Button again)
                {
                    SelectNavButton(again);
                    if (_vm != null && string.Equals(_currentNavTag, selectedTag, StringComparison.Ordinal))
                        ShowNavCount(again, _vm.Games.Count);
                }
                else
                {
                    _selectedNavButton = null;
                }
            }

            DumpSidebarDiag(panel);
        }

        /// <summary>
        /// Rebuilds the console list when it depends on what the library holds ("Hide consoles
        /// with no games"), so an import or a removal adds or drops rows straight away.
        /// </summary>
        private void RefreshSidebarForLibraryChange()
        {
            if (App.Configuration?.GetLibraryConfiguration().HideEmptyConsoles == true)
                BuildConsoleSidebar();
        }

        /// <summary>
        /// Game count per console, for the Preferences sidebar-layout editor, which marks the rows
        /// the "hide consoles with no games" option removes. The database stays private to the window.
        /// </summary>
        public Dictionary<string, int> GameCountsByConsole()
        {
            try { return _db?.GetGameCountsByConsole() ?? new(); }
            catch { return new(); }
        }

        private void SidebarGroupHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: SidebarGroupTag g }) return;
            bool collapse = g.Section.Visibility == Visibility.Visible;
            g.Section.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            g.Arrow.Text = collapse ? "▸" : "▾";
            if (collapse) _collapsedSidebarGroups.Add(g.Group);
            else _collapsedSidebarGroups.Remove(g.Group);
        }

        /// <summary>
        /// One console row. The shape is not cosmetic: FindSidebarButton and the console context
        /// menu identify a console by <c>CommandParameter</c>, and the nav badge is appended to
        /// the content panel.
        /// </summary>
        private Button MakeConsoleButton(ConsoleCatalogEntry entry, Style itemStyle)
        {
            var icon = new Image
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 8, 0),
                Source = SidebarIcon(entry.Tag),
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = entry.DisplayName, VerticalAlignment = VerticalAlignment.Center });

            var btn = new Button { Style = itemStyle, Content = row, CommandParameter = entry.Tag };
            btn.SetBinding(Button.CommandProperty, new Binding(nameof(MainViewModel.NavigateToConsoleCommand)));
            return btn;
        }

        private static ImageSource? SidebarIcon(string tag)
        {
            string? uri = ConsoleCatalog.IconUri(tag);
            if (uri == null) return null;
            if (_sidebarIcons.TryGetValue(uri, out var cached)) return cached;

            ImageSource? source = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(uri, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                source = bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Sidebar] icon {uri}: {ex.Message}");
            }
            _sidebarIcons[uri] = source;
            return source;
        }

        /// <summary>
        /// Records what the sidebar ACTUALLY contains once built — read back out of the panel
        /// itself, never from the catalog it was built from, so a renderer that drops, reorders
        /// or un-icons a row shows up here instead of being masked by the data it was given.
        /// Off unless EMUTASTIC_SIDEBAR_DIAG=1. Reads only.
        /// </summary>
        private static void DumpSidebarDiag(StackPanel panel)
        {
            if (Environment.GetEnvironmentVariable("EMUTASTIC_SIDEBAR_DIAG") != "1") return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== sidebar built {DateTime.Now:HH:mm:ss.fff} ===");
                int rows = 0, noIcon = 0;

                void DumpButton(Button b, string indent)
                {
                    rows++;
                    string tag = b.CommandParameter as string ?? "(NO CommandParameter)";
                    string label = "(NO label)";
                    bool hasIcon = false;
                    if (b.Content is StackPanel sp)
                    {
                        label = sp.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? label;
                        hasIcon = sp.Children.OfType<Image>().FirstOrDefault()?.Source != null;
                    }
                    if (!hasIcon) noIcon++;
                    sb.AppendLine($"{indent}{tag,-14} | {label,-22} | {(hasIcon ? "icon" : "MISSING")}");
                }

                foreach (var child in panel.Children)
                {
                    if (child is Button { Tag: SidebarGroupTag g } header)
                        sb.AppendLine($"  [{g.Group}] margin={header.Margin} expanded={g.Section.Visibility == Visibility.Visible}");
                    else if (child is Button b)
                        DumpButton(b, "  ");
                    else if (child is StackPanel section)
                        foreach (var c in section.Children.OfType<Button>()) DumpButton(c, "      ");
                    else
                        sb.AppendLine($"  ?? unexpected child: {child.GetType().Name}");
                }

                sb.AppendLine($"TOTAL console rows: {rows}   rows with no icon: {noIcon}");
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppPaths.GetFolder("Logs"), "sidebar-diag.log"), sb.ToString());
            }
            catch { /* never throw from diagnostics */ }
        }
    }
}
