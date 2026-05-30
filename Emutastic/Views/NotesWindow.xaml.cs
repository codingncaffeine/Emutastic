using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Emutastic.Models;
using Emutastic.Services;
using ICSharpCode.AvalonEdit.Search;

namespace Emutastic.Views
{
    /// <summary>
    /// Floating per-game notes editor (AvalonEdit). Autosaves with a debounce and
    /// persists to the Games.Notes column — which rides the existing library.db
    /// GitHub backup for free. One window per game (re-focused if already open).
    /// </summary>
    public partial class NotesWindow : FloatingToolWindow
    {
        private readonly Game _game;
        private readonly DatabaseService _db = new();
        private readonly DispatcherTimer _saveTimer;
        private SearchPanel? _searchPanel;
        private bool _suppressAutoSave;
        private bool _monospace = true;

        // One notes window per game id, app-wide. Reopening just re-focuses.
        private static readonly Dictionary<int, NotesWindow> _open = new();

        /// <summary>Opens (or re-focuses) the notes window for a game.</summary>
        public static void ShowFor(Game game, Window? owner, bool pinned = false)
        {
            if (_open.TryGetValue(game.Id, out var existing))
            {
                if (existing.IsRolledUp) existing.ToggleRollUp();
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var win = new NotesWindow(game);
            if (owner != null && !ReferenceEquals(owner, win)) win.Owner = owner;
            if (pinned) win.Topmost = true;   // open above a running game when launched in-game
            _open[game.Id] = win;
            win.Closed += (_, _) => _open.Remove(game.Id);
            win.Show();
            win.Activate();
        }

        public NotesWindow(Game game)
        {
            InitializeComponent();
            _game = game;
            TitleBar.TitleText = $"Notes — {game.Title}";

            Editor.LineNumbersForeground = (Brush)FindResource("TextMutedBrush");
            _searchPanel = SearchPanel.Install(Editor);

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistNotes(persistAsync: true); };

            // Load the existing note WITHOUT tripping autosave — the load itself raises
            // TextChanged, and an un-guarded handler would immediately write the loaded
            // (or empty) text back, which has wiped notes in this app before.
            _suppressAutoSave = true;
            Editor.Text = game.Notes ?? "";
            _suppressAutoSave = false;

            Editor.TextChanged += Editor_TextChanged;

            // Land the caret in the editor on open so the user can type immediately
            // (including when opened pinned over a running game from the cog menu).
            Loaded += (_, _) => Editor.Focus();
        }

        private void Editor_TextChanged(object? sender, EventArgs e)
        {
            if (_suppressAutoSave) return;
            SaveHint.Text = "Saving…";
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void PersistNotes(bool persistAsync)
        {
            string text = Editor.Text ?? "";
            _game.Notes = text;          // live-updates the detail-card preview (UI thread)
            int id = _game.Id;

            if (persistAsync)
            {
                // DB write off the UI thread; DatabaseService opens its own connection.
                Task.Run(() => { try { _db.UpdateNotes(id, text); } catch { } })
                    .ContinueWith(_ => Dispatcher.BeginInvoke(() => SaveHint.Text = "Saved"));
            }
            else
            {
                try { _db.UpdateNotes(id, text); } catch { }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _saveTimer.Stop();
            PersistNotes(persistAsync: false);   // final flush synchronously before close
            base.OnClosing(e);
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            _searchPanel?.Open();
            Editor.TextArea.Focus();
        }

        private void Wrap_Click(object sender, RoutedEventArgs e)
        {
            Editor.WordWrap = !Editor.WordWrap;
            WrapBtn.Content = Editor.WordWrap ? "Word Wrap: On" : "Word Wrap: Off";
        }

        private void Font_Click(object sender, RoutedEventArgs e)
        {
            _monospace = !_monospace;
            Editor.FontFamily = new FontFamily(_monospace ? "Consolas" : "Segoe UI");
            FontBtn.Content = _monospace ? "Monospace" : "Proportional";
        }
    }
}
