using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IoPath = System.IO.Path;
using System.Windows.Shapes;
using Emutastic.Models;
using Emutastic.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Emutastic.Views
{
    public partial class ThemeEditorWindow : Window
    {
        /// <summary>Working copy of colors being edited.</summary>
        private ThemeColors _editColors = null!;

        /// <summary>Map of token name → (swatch Rectangle, hex TextBox).</summary>
        private readonly Dictionary<string, (Rectangle Swatch, TextBox Hex)> _editors = new();

        /// <summary>Suppress preview updates during bulk load.</summary>
        private bool _loading;

        public ThemeEditorWindow()
        {
            InitializeComponent();
            PopulateBaseThemeCombo();
            LoadFromActiveTheme();
        }

        // ── Initialization ──────────────────────────────────────────────

        private void PopulateBaseThemeCombo()
        {
            var themes = ThemeService.Instance.GetAvailableThemes();
            foreach (var (id, name) in themes)
            {
                BaseThemeCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            }

            // Select current active theme
            for (int i = 0; i < BaseThemeCombo.Items.Count; i++)
            {
                if (BaseThemeCombo.Items[i] is ComboBoxItem item &&
                    item.Tag is string id && id == ThemeService.Instance.ActiveThemeId)
                {
                    BaseThemeCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        private void LoadFromActiveTheme()
        {
            var themeId = ThemeService.Instance.ActiveThemeId;
            LoadThemeColors(themeId);
        }

        private void LoadThemeColors(string themeId)
        {
            _loading = true;

            // Get the color set for the selected base theme
            var themes = new Dictionary<string, Func<ThemeColors>>
            {
                ["builtin.dark"] = ThemeService.GetDefaultColors,
                ["builtin.light"] = () => GetThemeColorsByReflection("GetLightColors"),
                ["builtin.oled"] = () => GetThemeColorsByReflection("GetOledColors"),
                ["builtin.midnight"] = () => GetThemeColorsByReflection("GetMidnightColors"),
            };

            _editColors = themes.TryGetValue(themeId, out var factory)
                ? factory()
                : ThemeService.GetDefaultColors();

            BuildColorEditors();
            UpdatePreview();
            _loading = false;
        }

        private static ThemeColors GetThemeColorsByReflection(string methodName)
        {
            var method = typeof(ThemeService).GetMethod(methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
            return method?.Invoke(null, null) as ThemeColors ?? ThemeService.GetDefaultColors();
        }

        // ── Color editor building ───────────────────────────────────────

        private static readonly (string Category, (string Token, string Label)[] Items)[] ColorGroups =
        {
            ("CORE PALETTE", new[]
            {
                ("BgPrimary", "Background"),
                ("BgSecondary", "Sidebar / Secondary"),
                ("BgTertiary", "Tertiary"),
                ("BgQuaternary", "Quaternary"),
                ("BorderSubtle", "Border (subtle)"),
                ("BorderNormal", "Border"),
                ("TextPrimary", "Text"),
                ("TextSecondary", "Text (secondary)"),
                ("TextMuted", "Text (muted)"),
                ("Accent", "Accent"),
                ("AccentHover", "Accent (hover)"),
                ("Green", "Green"),
            }),
            ("ACCENT VARIANTS", new[]
            {
                ("AccentPressed", "Accent (pressed)"),
                ("AccentDisabled", "Accent (disabled)"),
            }),
            ("SCROLLBAR", new[]
            {
                ("ScrollThumb", "Thumb"),
                ("ScrollThumbHover", "Thumb (hover)"),
                ("ScrollThumbDrag", "Thumb (drag)"),
                ("ScrollTrack", "Track"),
            }),
            ("PLAY BUTTON", new[]
            {
                ("PlayBtnBg", "Background"),
                ("PlayBtnBorder", "Border"),
                ("PlayBtnHoverBg", "Hover bg"),
                ("PlayBtnHoverBorder", "Hover border"),
                ("PlayBtnPressedBg", "Pressed bg"),
            }),
            ("WINDOW BUTTONS", new[]
            {
                ("TrafficYellow", "Minimize"),
                ("TrafficYellowHover", "Minimize (hover)"),
                ("TrafficGreenHover", "Maximize (hover)"),
                ("TrafficRed", "Close"),
                ("TrafficRedHover", "Close (hover)"),
            }),
            ("PILL CONTROLS", new[]
            {
                ("PillBg", "Background"),
                ("PillBorder", "Border"),
                ("PillHoverBg", "Hover"),
                ("PillPressedBg", "Pressed"),
                ("PillFg", "Foreground"),
                ("PillMutedFg", "Muted fg"),
                ("PillGroupBg", "Group bg"),
            }),
            ("SURFACES", new[]
            {
                ("Surface", "Surface"),
                ("SurfaceHover", "Surface (hover)"),
                ("SurfaceActive", "Surface (active)"),
                ("ContentBg", "Content bg"),
            }),
            ("OTHER", new[]
            {
                ("OverlayBg", "Overlay bg"),
                ("Shadow", "Shadow"),
                ("Warning", "Warning"),
                ("AchievementGold", "Achievement gold"),
                ("FavoriteHeart", "Favorite heart"),
            }),
        };

        private void BuildColorEditors()
        {
            ColorEditorPanel.Children.Clear();
            _editors.Clear();

            foreach (var (category, items) in ColorGroups)
            {
                // Category header
                var header = new TextBlock
                {
                    Text = category,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FindBrush("TextMutedBrush"),
                    Margin = new Thickness(0, 16, 0, 8),
                };
                ColorEditorPanel.Children.Add(header);

                var separator = new Rectangle
                {
                    Height = 1,
                    Fill = FindBrush("BorderSubtleBrush"),
                    Margin = new Thickness(0, 0, 0, 8),
                };
                ColorEditorPanel.Children.Add(separator);

                foreach (var (token, label) in items)
                {
                    var row = CreateColorRow(token, label);
                    ColorEditorPanel.Children.Add(row);
                }
            }
        }

        private Grid CreateColorRow(string tokenName, string label)
        {
            var currentHex = GetColorValue(tokenName) ?? "#FF00FF";

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Label
            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = FindBrush("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(labelBlock, 0);
            grid.Children.Add(labelBlock);

            // Hex input
            var hexBox = new TextBox
            {
                Text = currentHex,
                Width = 80,
                Height = 26,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Background = FindBrush("BgTertiaryBrush"),
                Foreground = FindBrush("TextPrimaryBrush"),
                BorderBrush = FindBrush("BorderNormalBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Tag = tokenName,
            };
            hexBox.TextChanged += HexBox_TextChanged;
            Grid.SetColumn(hexBox, 1);
            grid.Children.Add(hexBox);

            // Color swatch (clickable)
            var swatch = new Rectangle
            {
                Width = 26,
                Height = 26,
                RadiusX = 4,
                RadiusY = 4,
                Stroke = FindBrush("BorderNormalBrush"),
                StrokeThickness = 1,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = tokenName,
            };
            try { swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(currentHex)); }
            catch { swatch.Fill = Brushes.Magenta; }
            swatch.MouseLeftButtonDown += Swatch_Click;
            Grid.SetColumn(swatch, 2);
            grid.Children.Add(swatch);

            _editors[tokenName] = (swatch, hexBox);
            return grid;
        }

        private Brush FindBrush(string key)
        {
            return TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        // ── Color editing ───────────────────────────────────────────────

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (sender is not TextBox tb || tb.Tag is not string token) return;

            var hex = tb.Text.Trim();
            if (!IsValidHex(hex)) return;

            SetColorValue(token, hex);

            // Update swatch
            if (_editors.TryGetValue(token, out var pair))
            {
                try { pair.Swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                catch { }
            }

            UpdatePreview();
        }

        private void Swatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Rectangle rect || rect.Tag is not string token) return;

            var currentHex = GetColorValue(token) ?? "#FFFFFF";
            Color currentColor;
            try { currentColor = (Color)ColorConverter.ConvertFromString(currentHex); }
            catch { currentColor = Colors.White; }

            var dlg = new WinForms.ColorDialog
            {
                Color = System.Drawing.Color.FromArgb(currentColor.R, currentColor.G, currentColor.B),
                FullOpen = true,
                AnyColor = true,
            };

            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                var c = dlg.Color;
                var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                SetColorValue(token, hex);

                // Update UI
                if (_editors.TryGetValue(token, out var pair))
                {
                    _loading = true;
                    pair.Hex.Text = hex;
                    pair.Swatch.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                    _loading = false;
                }

                UpdatePreview();
            }
        }

        private static bool IsValidHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return false;
            try { ColorConverter.ConvertFromString(hex); return true; }
            catch { return false; }
        }

        // ── ThemeColors property access ─────────────────────────────────

        private string? GetColorValue(string tokenName)
        {
            var prop = typeof(ThemeColors).GetProperty(tokenName);
            return prop?.GetValue(_editColors) as string;
        }

        private void SetColorValue(string tokenName, string hex)
        {
            var prop = typeof(ThemeColors).GetProperty(tokenName);
            prop?.SetValue(_editColors, hex);
        }

        // ── Live preview ────────────────────────────────────────────────

        private void UpdatePreview()
        {
            if (_editColors == null) return;
            var d = ThemeService.GetDefaultColors();

            Color Parse(string? hex, string? fallback = null)
            {
                try { return (Color)ColorConverter.ConvertFromString(hex ?? fallback ?? "#FF00FF"); }
                catch { return Colors.Magenta; }
            }

            var bgPrimary = Parse(_editColors.BgPrimary, d.BgPrimary);
            var bgSecondary = Parse(_editColors.BgSecondary, d.BgSecondary);
            var bgTertiary = Parse(_editColors.BgTertiary, d.BgTertiary);
            var bgQuat = Parse(_editColors.BgQuaternary, d.BgQuaternary);
            var textPrimary = Parse(_editColors.TextPrimary, d.TextPrimary);
            var textSecondary = Parse(_editColors.TextSecondary, d.TextSecondary);
            var textMuted = Parse(_editColors.TextMuted, d.TextMuted);
            var accent = Parse(_editColors.Accent, d.Accent);
            var pillBg = Parse(_editColors.PillBg, d.PillBg);
            var pillFg = Parse(_editColors.PillFg, d.PillFg);
            var surface = Parse(_editColors.Surface, d.Surface);
            var borderNormal = Parse(_editColors.BorderNormal, d.BorderNormal);
            var contentBg = Parse(_editColors.ContentBg, d.ContentBg);

            // Sidebar
            PreviewSidebar.Background = new SolidColorBrush(bgSecondary);
            PreviewAppTitle.Foreground = new SolidColorBrush(textPrimary);

            // Active nav item
            PreviewNavActive.Background = new SolidColorBrush(accent);
            if (PreviewNavActive.Child is TextBlock navActiveText)
                navActiveText.Foreground = Brushes.White;

            // Inactive nav items
            PreviewNavText1.Foreground = new SolidColorBrush(textSecondary);
            PreviewNavText2.Foreground = new SolidColorBrush(textSecondary);
            PreviewNavText3.Foreground = new SolidColorBrush(textSecondary);

            // Content area
            PreviewContent.Background = new SolidColorBrush(bgPrimary);
            PreviewToolbarTitle.Foreground = new SolidColorBrush(textPrimary);

            // Pills
            PreviewPill1.Background = new SolidColorBrush(pillBg);
            PreviewPillText1.Foreground = new SolidColorBrush(pillFg);
            PreviewPill2.Background = new SolidColorBrush(pillBg);
            PreviewPillText2.Foreground = new SolidColorBrush(pillFg);

            // Game cards
            var cardBrush = new SolidColorBrush(bgTertiary);
            PreviewCard1.Background = cardBrush;
            PreviewCard2.Background = cardBrush;
            PreviewCard3.Background = cardBrush;
            PreviewCard4.Background = cardBrush;

            // Accent button
            PreviewAccentBtn.Background = new SolidColorBrush(accent);
            PreviewAccentBtnText.Foreground = Brushes.White;

            // Secondary button
            PreviewSecondaryBtn.Background = new SolidColorBrush(surface);
            PreviewSecondaryBtn.BorderBrush = new SolidColorBrush(borderNormal);
            PreviewSecondaryBtnText.Foreground = new SolidColorBrush(textPrimary);

            // Text samples
            PreviewTextPrimary.Foreground = new SolidColorBrush(textPrimary);
            PreviewTextSecondary.Foreground = new SolidColorBrush(textSecondary);
            PreviewTextMuted.Foreground = new SolidColorBrush(textMuted);
        }

        // ── Base theme switching ────────────────────────────────────────

        private void BaseThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (BaseThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                LoadThemeColors(id);
            }
        }

        // ── Bottom bar actions ──────────────────────────────────────────

        private void ApplyBtn_Click(object sender, RoutedEventArgs e)
        {
            // Push current edit colors to ThemeService
            ThemeService.Instance.ApplyEditedColors(_editColors);

            // Save the active theme ID to config
            if (App.Configuration != null)
            {
                var theme = App.Configuration.GetThemeConfiguration();
                theme.ActiveThemeId = ThemeService.Instance.ActiveThemeId;
                App.Configuration.SetThemeConfiguration(theme);
                _ = App.Configuration.SaveAsync();
            }

            MessageBox.Show("Theme applied! Reopen windows to see full changes.",
                "Theme Editor", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Reset all colors to the Dark (Default) theme?",
                "Reset Theme", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                LoadThemeColors("builtin.dark");

                // Also select Dark in the base combo
                for (int i = 0; i < BaseThemeCombo.Items.Count; i++)
                {
                    if (BaseThemeCombo.Items[i] is ComboBoxItem item && item.Tag is string id && id == "builtin.dark")
                    {
                        _loading = true;
                        BaseThemeCombo.SelectedIndex = i;
                        _loading = false;
                        break;
                    }
                }
            }
        }

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Emutastic Theme (*.emutheme)|*.emutheme",
                FileName = "MyTheme.emutheme",
                Title = "Export Theme",
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                var manifest = new ThemeManifest
                {
                    Id = $"custom.{IoPath.GetFileNameWithoutExtension(dlg.FileName).ToLowerInvariant().Replace(" ", "-")}",
                    Name = IoPath.GetFileNameWithoutExtension(dlg.FileName),
                    Author = Environment.UserName,
                    Version = "1.0.0",
                    Description = "Custom theme created with Emutastic Theme Editor",
                    ApiVersion = 1,
                };

                using var stream = File.Create(dlg.FileName);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

                // Write theme.json
                var manifestEntry = zip.CreateEntry("theme.json");
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                }

                // Include background image if configured
                var themeCfg = App.Configuration?.GetThemeConfiguration();
                var exportColors = JsonSerializer.Deserialize<ThemeColors>(
                    JsonSerializer.Serialize(_editColors)) ?? _editColors; // clone

                if (themeCfg != null && !string.IsNullOrWhiteSpace(themeCfg.BackgroundImagePath)
                    && File.Exists(themeCfg.BackgroundImagePath))
                {
                    var imgFileName = IoPath.GetFileName(themeCfg.BackgroundImagePath);
                    var assetPath = $"assets/{imgFileName}";
                    zip.CreateEntryFromFile(themeCfg.BackgroundImagePath, assetPath);
                    exportColors.BackgroundImage = assetPath;
                    exportColors.BackgroundImageOpacity = themeCfg.BackgroundImageOpacity;
                    exportColors.BackgroundImageStretch = themeCfg.BackgroundImageStretch;
                }

                // Write colors.json
                var colorsEntry = zip.CreateEntry("colors.json");
                using (var writer = new StreamWriter(colorsEntry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(exportColors, new JsonSerializerOptions { WriteIndented = true }));
                }

                MessageBox.Show($"Theme exported to:\n{dlg.FileName}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Emutastic Theme (*.emutheme)|*.emutheme|JSON Color File (*.json)|*.json",
                Title = "Import Theme",
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                ThemeColors? imported = null;

                if (dlg.FileName.EndsWith(".emutheme", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(dlg.FileName);
                    var colorsEntry = zip.GetEntry("colors.json");
                    if (colorsEntry != null)
                    {
                        using var reader = new StreamReader(colorsEntry.Open());
                        imported = JsonSerializer.Deserialize<ThemeColors>(reader.ReadToEnd());
                    }
                }
                else
                {
                    var json = File.ReadAllText(dlg.FileName);
                    imported = JsonSerializer.Deserialize<ThemeColors>(json);
                }

                if (imported == null)
                {
                    MessageBox.Show("Could not read colors from the theme file.",
                        "Import Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Merge imported colors into edit state
                _loading = true;
                _editColors = imported;
                BuildColorEditors();
                UpdatePreview();
                _loading = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Import failed: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
