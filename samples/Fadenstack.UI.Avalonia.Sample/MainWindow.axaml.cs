using Avalonia.Controls;
using Fadenstack.UI.Avalonia.Views;

namespace Fadenstack.UI.Avalonia.Sample;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    internal ChatView ChatPanel => Chat;
}
