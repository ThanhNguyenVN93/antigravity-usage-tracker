using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AntigravityUsageTracker.Services;

namespace AntigravityUsageTracker.Views;

public partial class CompactToastWindow : Window
{
    private static readonly Brush Ok = Frozen("#A6E3A1");
    private static readonly Brush Bad = Frozen("#F38BA8");
    private static readonly Brush Normal = Frozen("#CDD6F4");

    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    public CompactToastWindow()
    {
        InitializeComponent();
        TxtTitle.Text = Lang.T("compact.title");
        TxtNote.Text = Lang.T("compact.note");
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); Close(); };
        Closed += (_, _) => _closeTimer.Stop();
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private void Place()
    {
        var wa = ScreenPlacement.WorkAreaForCursor(this);
        Left = wa.Right - ActualWidth - 12;
        Top = wa.Bottom - ActualHeight - 12;
    }

    public void SetRunning(string sessionId)
    {
        _closeTimer.Stop();
        TxtStatus.Foreground = Normal;
        string shortId = sessionId.Length > 8 ? sessionId.Substring(0, 8) : sessionId;
        TxtStatus.Text = Lang.T("compact.running", shortId);
    }

    public void SetDone(bool ok, string statusText)
    {
        TxtStatus.Foreground = ok ? Ok : Bad;
        TxtStatus.Text = statusText;
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
