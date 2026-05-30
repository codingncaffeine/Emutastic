using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Emutastic.Models;
using Emutastic.Services;
using Microsoft.Web.WebView2.Core;

namespace Emutastic.Views
{
    /// <summary>
    /// Floating PDF manual viewer. Hosts a bundled PDF.js viewer inside WebView2 —
    /// PDF.js (rather than WebView2's built-in PDF UI) because only PDF.js exposes
    /// the current page/zoom, which the resume-reading feature needs. Restores and
    /// persists the last-read page + zoom per game.
    /// </summary>
    public partial class ManualViewerWindow : FloatingToolWindow
    {
        private readonly Game _game;
        private readonly DatabaseService _db = new();
        private readonly string _manualPath;
        private readonly string _manualFile;
        private readonly DispatcherTimer _saveTimer;
        private readonly Microsoft.Web.WebView2.Wpf.WebView2 Web;

        private int _pendingPage = 1;
        private string _pendingZoom = "auto";
        private bool _havePending;

        // One viewer per game id, app-wide. Reopening re-focuses.
        private static readonly Dictionary<int, ManualViewerWindow> _open = new();

        /// <summary>Opens (or re-focuses) the manual viewer for a game that has a downloaded manual.</summary>
        public static void ShowFor(Game game, Window? owner, bool pinned = false)
        {
            if (_open.TryGetValue(game.Id, out var existing))
            {
                if (existing.IsRolledUp) existing.ToggleRollUp();
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                return;
            }

            var win = new ManualViewerWindow(game);
            if (owner != null) win.Owner = owner;
            if (pinned) win.Topmost = true;
            _open[game.Id] = win;
            win.Closed += (_, _) => _open.Remove(game.Id);
            win.Show();
            win.Activate();
        }

        public ManualViewerWindow(Game game)
        {
            InitializeComponent();
            _game = game;
            _manualPath = game.ManualPath ?? "";
            _manualFile = string.IsNullOrEmpty(_manualPath) ? "manual.pdf" : Path.GetFileName(_manualPath);
            TitleBar.TitleText = $"Manual — {game.Title}";

            Web = new Microsoft.Web.WebView2.Wpf.WebView2();
            // Dark default background so there's no white flash before/around the PDF.
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x0F, 0x10);
            WebHost.Children.Add(Web);

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); FlushPosition(persistAsync: true); };

            Loaded += async (_, _) => await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            if (string.IsNullOrEmpty(_manualPath) || !File.Exists(_manualPath))
            {
                FallbackOrClose("The manual file is missing.");
                return;
            }

            try
            {
                // WebView2 needs a writable user-data folder. The default (next to the
                // exe) fails in a read-only install dir, so pin it under the data root.
                string udf = AppPaths.GetFolder("WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf, null);
                await Web.EnsureCoreWebView2Async(env);

                var core = Web.CoreWebView2;
                // Force the PDF.js viewer into dark mode so its toolbar matches the app —
                // otherwise it renders a light/white toolbar strip across the top.
                try { core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark; } catch { }

                string pdfjsRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "pdfjs");
                string manualDir = Path.GetDirectoryName(_manualPath)!;

                // Serve the viewer and the PDF from virtual https origins (file:// breaks
                // PDF.js workers/CORS). Allow = the viewer origin may fetch the PDF origin.
                core.SetVirtualHostNameToFolderMapping("pdfjs.example", pdfjsRoot, CoreWebView2HostResourceAccessKind.Allow);
                core.SetVirtualHostNameToFolderMapping("manual.example", manualDir, CoreWebView2HostResourceAccessKind.Allow);

                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true; // copy/print within the PDF
                core.NewWindowRequested += (_, e) => e.Handled = true; // no popups
                core.WebMessageReceived += OnWebMessage;

                var pos = _db.GetManualReadingState(_game.Id, _manualFile);
                int page = pos?.Page ?? 1;
                string zoom = string.IsNullOrWhiteSpace(pos?.Zoom) ? "auto" : pos!.Value.Zoom;

                string manualUrl = "https://manual.example/" + Uri.EscapeDataString(_manualFile);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildBridgeScript(manualUrl, page, zoom));

                core.Navigate("https://pdfjs.example/web/viewer.html");
            }
            catch (Exception ex)
            {
                FallbackOrClose($"Couldn't start the viewer ({ex.Message}).");
            }
        }

        /// <summary>
        /// JS injected into the PDF.js viewer: opens the PDF (programmatic open avoids
        /// the viewer's same-origin file check), restores the saved page/zoom once the
        /// document loads, and posts page/zoom changes back to the host for persistence.
        /// </summary>
        private static string BuildBridgeScript(string manualUrl, int page, string zoom)
        {
            string urlJs  = JsonSerializer.Serialize(manualUrl);
            string zoomJs = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(zoom) ? "auto" : zoom);
            return
                "(function(){var U=" + urlJs + ",RP=" + page + ",RZ=" + zoomJs + ";" +
                "function hook(){var a=window.PDFViewerApplication;" +
                "if(!a||!a.initializedPromise){setTimeout(hook,100);return;}" +
                "a.initializedPromise.then(function(){start(a);});}" +
                "function start(a){try{a.open({url:U});}catch(e){}" +
                "var done=false;a.eventBus.on('pagesloaded',function(){if(done)return;done=true;" +
                "try{if(RP>1)a.page=RP;if(RZ)a.pdfViewer.currentScaleValue=RZ;}catch(e){}});" +
                "var lp=0,lz='';function post(){try{var p=a.page||1," +
                "z=(a.pdfViewer&&a.pdfViewer.currentScaleValue)||'auto';" +
                "if(p===lp&&z===lz)return;lp=p;lz=z;" +
                "window.chrome.webview.postMessage({type:'pos',page:p,zoom:''+z});}catch(e){}}" +
                "a.eventBus.on('pagechanging',post);a.eventBus.on('scalechanging',post);}" +
                "hook();})();";
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var node = JsonNode.Parse(e.WebMessageAsJson);
                if (node?["type"]?.GetValue<string>() != "pos") return;
                _pendingPage = node["page"]?.GetValue<int>() ?? 1;
                _pendingZoom = node["zoom"]?.GetValue<string>() ?? "auto";
                _havePending = true;
                _saveTimer.Stop();
                _saveTimer.Start();
            }
            catch { /* malformed message — ignore */ }
        }

        private void FlushPosition(bool persistAsync)
        {
            if (!_havePending) return;
            int id = _game.Id;
            string file = _manualFile;
            int page = _pendingPage;
            string zoom = _pendingZoom;
            if (persistAsync)
                Task.Run(() => { try { _db.SaveManualReadingState(id, file, page, 0.0, zoom); } catch { } });
            else
                try { _db.SaveManualReadingState(id, file, page, 0.0, zoom); } catch { }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _saveTimer.Stop();
            FlushPosition(persistAsync: false);   // final flush synchronously
            try { Web?.Dispose(); } catch { }
            base.OnClosing(e);
        }

        private void FallbackOrClose(string reason)
        {
            // WebView2 runtime missing, or a file problem — open in the system PDF app.
            try
            {
                if (File.Exists(_manualPath))
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(_manualPath) { UseShellExecute = true });
            }
            catch { }
            MessageBox.Show(reason + "\n\nOpened it in your default PDF viewer instead.", "Manual",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }
}
