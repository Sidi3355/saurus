using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Saurus.Guardrails;

namespace Saurus.Ui;

/// <summary>
/// First-run key entry.
///
/// The CLI route (an environment variable and a command-line flag) is fine for the person
/// who built the thing and unusable for anyone you hand it to. This is the one piece of
/// settings UI the app has, and it exists because the alternative is a friend downloading an
/// installer and finding a tray icon that does nothing.
///
/// The key goes straight from the box into DPAPI. It is never shown, echoed, logged or held
/// anywhere else — the field is a PasswordBox and its contents are cleared on close.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly PasswordBox _key;
    private readonly TextBlock _error;
    private readonly string _dir;

    public bool KeySaved { get; private set; }

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public SetupWindow(string dir)
    {
        _dir = dir;

        Title = "SAURUS - setup";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B("SurfaceBg");
        ShowInTaskbar = true;

        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };

        stack.Children.Add(new TextBlock
        {
            Text = "SAURUS needs an Anthropic API key",
            Foreground = B("TextBody"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Create one at console.anthropic.com, then paste it below. It is encrypted " +
                   "with Windows DPAPI for your user account and stored locally. It is only " +
                   "ever sent to api.anthropic.com, and never appears in logs.",
            Foreground = B("TextDim"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
            LineHeight = 18
        });

        var link = new TextBlock
        {
            Text = "Open console.anthropic.com",
            Foreground = B("Accent"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 6, 0, 16)
        };
        link.MouseLeftButtonUp += (_, _) => OpenConsole();
        stack.Children.Add(link);

        _key = new PasswordBox
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(11, 9, 11, 9),
            Background = B("SurfaceInput"),
            Foreground = B("TextBody"),
            BorderBrush = B("Hairline"),
            BorderThickness = new Thickness(1),
            CaretBrush = B("Accent")
        };
        _key.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        stack.Children.Add(_key);

        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(_error);

        var save = new Button
        {
            Content = "Save and start",
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 12.5
        };
        save.Click += (_, _) => Save();

        var later = new Button
        {
            Content = "Later",
            Style = (Style)Application.Current.FindResource("FlatButton"),
            Padding = new Thickness(14, 8, 14, 8),
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 0, 0)
        };
        later.Click += (_, _) => Close();

        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { save, later }
        });

        Content = stack;

        Loaded += (_, _) => _key.Focus();

        // The key never outlives the window.
        Closed += (_, _) => _key.Clear();
    }

    private void Save()
    {
        var value = _key.Password;

        if (string.IsNullOrWhiteSpace(value))
        {
            Fail("Paste a key first.");
            return;
        }

        if (!ApiKeyStore.LooksPlausible(value.Trim()))
        {
            // A warning, not a block: the expected prefix could change, and refusing a valid
            // key because it did not match a hard-coded shape would be worse than a bad call
            // returning 401 with a clear message.
            Fail("That does not look like an Anthropic key (they start sk-ant-). Press Save again to use it anyway.");
            if (!_warned) { _warned = true; return; }
        }

        try
        {
            ApiKeyStore.Save(_dir, value.Trim());
            KeySaved = true;
            _key.Clear();
            Close();
        }
        catch (Exception ex)
        {
            Fail($"Could not store the key: {ex.Message}");
        }
    }

    private bool _warned;

    private void Fail(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private static void OpenConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://console.anthropic.com/settings/keys")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not open the browser", ex);
        }
    }
}
