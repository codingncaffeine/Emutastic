using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Emutastic.Views
{
    /// <summary>
    /// Reusable custom title bar for <see cref="FloatingToolWindow"/> hosts. Renders
    /// the title plus a stay-on-top pin, WindowShade roll-up, minimize and close,
    /// and routes the actions to whichever FloatingToolWindow it lives in. Shared by
    /// the Notes window and the manual viewer so they look and behave identically.
    /// </summary>
    public partial class ToolWindowTitleBar : UserControl
    {
        public ToolWindowTitleBar()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        public static readonly DependencyProperty TitleTextProperty =
            DependencyProperty.Register(nameof(TitleText), typeof(string), typeof(ToolWindowTitleBar),
                new PropertyMetadata("", OnTitleChanged));

        public string TitleText
        {
            get => (string)GetValue(TitleTextProperty);
            set => SetValue(TitleTextProperty, value);
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ToolWindowTitleBar b) b.TitleLabel.Text = (string)e.NewValue;
        }

        private FloatingToolWindow? Win => Window.GetWindow(this) as FloatingToolWindow;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Win is { } w)
            {
                w.PinChanged += (_, _) => UpdatePinVisual();
                UpdatePinVisual();
            }
        }

        private void UpdatePinVisual()
        {
            bool pinned = Win?.Topmost == true;
            PinBtn.Foreground = pinned
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextSecondaryBrush");
            PinBtn.ToolTip = pinned ? "Pinned on top — click to unpin" : "Stay on top";
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { Win?.ToggleRollUp(); return; }
            Win?.BeginDrag();
        }

        private void Pin_Click(object sender, RoutedEventArgs e) => Win?.TogglePin();
        private void RollUp_Click(object sender, RoutedEventArgs e) => Win?.ToggleRollUp();
        private void Min_Click(object sender, RoutedEventArgs e)
        {
            if (Win is { } w) w.WindowState = WindowState.Minimized;
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Win?.Close();
    }
}
