using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Emutastic.Views.PauseEffects
{
    /// <summary>
    /// Drives a pause effect via <see cref="CompositionTarget.Rendering"/> and
    /// fades the host in/out when the active effect starts or stops. Owns the
    /// effect lifecycle — Init / Tick / Dispose calls all originate here.
    /// </summary>
    public sealed class PauseEffectRunner : IDisposable
    {
        private readonly PauseEffectHost _host;
        private IPauseEffect? _vectorEffect;
        private IPixelPauseEffect? _pixelEffect;
        private TimeSpan _lastFrame;
        private bool _running;
        private double _intensity = 1.0;
        // Generation counter for fade-out completion guards. Bumped on every
        // Stop()/Start() so a deferred FadeOut callback for an OLD effect can't
        // dispose the NEW one (rapid pause-toggle race).
        private int _stopGen;

        // Internal pixel-buffer resolution for IPixelPauseEffect. Coarse on purpose:
        // plasma / aurora look correct at 320×240 and the upscale via Image.Stretch
        // keeps per-frame cost negligible even on Vulkan-overlay-window setups.
        private const int PixelEffectWidth  = 320;
        private const int PixelEffectHeight = 240;

        public PauseEffectRunner(PauseEffectHost host)
        {
            _host = host;
        }

        public void Start(IPauseEffect effect, double intensity)
        {
            Stop();
            _stopGen++;
            _host.BeginAnimation(UIElement.OpacityProperty, null); // kill any pending fade
            // Clear the other-flavor field so OnRendering doesn't tick the previous
            // effect after a cross-flavor switch (vector → pixel left _vectorEffect
            // non-null and the pixel branch never ran — Plasma/Aurora stayed empty).
            _pixelEffect = null;
            _vectorEffect = effect;
            _intensity = intensity;
            _host.UseVectorPath();
            _host.Visibility = Visibility.Visible;
            _host.UpdateLayout(); // ensure ActualWidth/Height before Init reads them
            _vectorEffect.Init(GetCanvasSize(), intensity);
            BeginRendering();
            FadeIn();
        }

        public void Start(IPixelPauseEffect effect, double intensity)
        {
            Stop();
            _stopGen++;
            _host.BeginAnimation(UIElement.OpacityProperty, null);
            _vectorEffect = null;
            _pixelEffect = effect;
            _intensity = intensity;
            _host.UsePixelPath(PixelEffectWidth, PixelEffectHeight);
            _host.Visibility = Visibility.Visible;
            _pixelEffect.Init(PixelEffectWidth, PixelEffectHeight, intensity);
            BeginRendering();
            FadeIn();
        }

        public void Stop()
        {
            if (!_running) { DisposeEffect(); return; }
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
            int gen = ++_stopGen;
            // Fade host out, then drop the effect when fade completes — but only
            // if no Start() has happened in the meantime (rapid pause-toggle).
            FadeOut(() =>
            {
                if (gen != _stopGen) return; // a new effect started during the fade
                DisposeEffect();
                _host.Clear();
                _host.Visibility = Visibility.Collapsed;
            });
        }

        private void DisposeEffect()
        {
            try { _vectorEffect?.Dispose(); } catch { }
            try { _pixelEffect?.Dispose(); } catch { }
            _vectorEffect = null;
            _pixelEffect = null;
        }

        public void Resize()
        {
            if (_vectorEffect != null) _vectorEffect.Init(GetCanvasSize(), _intensity);
            // Pixel buffer dimensions are fixed; no re-init needed there.
        }

        private Size GetCanvasSize()
        {
            double w = _host.ActualWidth  > 0 ? _host.ActualWidth  : 800;
            double h = _host.ActualHeight > 0 ? _host.ActualHeight : 600;
            return new Size(w, h);
        }

        private void BeginRendering()
        {
            if (_running) return;
            _running = true;
            _lastFrame = TimeSpan.Zero;
            _host.Visibility = Visibility.Visible;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_running) return;
            var t = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
            double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60.0 : (t - _lastFrame).TotalSeconds;
            // Clamp delta so a long stall doesn't fast-forward 5 seconds of physics
            // into a single frame (would teleport every particle off-screen).
            if (dt > 0.1) dt = 0.1;
            _lastFrame = t;

            try
            {
                if (_vectorEffect != null)
                {
                    var dc = _host.GetVectorContext();
                    _vectorEffect.Tick(dt, dc);
                    _host.EndVectorContext();
                }
                else if (_pixelEffect != null && _host.UsePixelPath(PixelEffectWidth, PixelEffectHeight) is WriteableBitmap bmp)
                {
                    _pixelEffect.Tick(dt, bmp);
                }
            }
            catch (Exception ex)
            {
                // Don't let a misbehaving effect crash the whole emulator window.
                System.Diagnostics.Trace.WriteLine($"PauseEffect tick failed: {ex.Message}");
                Stop();
            }
        }

        // Smooth visual transitions so the effect doesn't pop on/off at full opacity.
        private void FadeIn()
        {
            _host.BeginAnimation(UIElement.OpacityProperty, null);
            _host.Opacity = 0;
            _host.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        }

        private void FadeOut(Action onComplete)
        {
            var anim = new DoubleAnimation(_host.Opacity, 0, TimeSpan.FromMilliseconds(250));
            anim.Completed += (_, _) => onComplete();
            _host.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnRendering;
            _running = false;
            DisposeEffect();
        }
    }
}
