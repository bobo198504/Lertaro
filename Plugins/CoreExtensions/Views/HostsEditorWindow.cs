using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lertaro.PluginSdk.Services;

namespace Lertaro.Plugins.CoreExtensions.Views;

internal sealed class HostsEditorWindow : Window
{
    private static HostsEditorWindow? _open;

    private readonly TextBox _editor;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _reload;
    private string _loadedText = string.Empty;
    private bool _loading;
    private bool _saving;

    internal static void ShowEditor()
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing._editor.Focus();
            return;
        }

        var window = new HostsEditorWindow();
        _open = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, window))
                _open = null;
        };
        window.Show();
        window.Activate();
    }

    private HostsEditorWindow()
    {
        Title = TranslationService.Get("HostsEditor_WindowTitle");
        Width = 920;
        Height = 680;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        if (Application.Current.TryFindResource("CardBackground") is Brush background)
            Background = background;
        if (Application.Current.TryFindResource("TextPrimary") is Brush foreground)
            Foreground = foreground;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = TranslationService.Get("HostsFile_Title"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = HostsFileStore.HostsPath,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.65,
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Padding = new Thickness(12),
        };
        _editor.SetResourceReference(Control.BackgroundProperty, "ContentBg");
        _editor.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        _editor.TextChanged += (_, _) =>
        {
            if (!_loading)
                UpdateStatus();
        };
        Grid.SetRow(_editor, 1);
        root.Children.Add(_editor);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(_status, 0);
        footer.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _save = CreateButton("HostsEditor_Save", OnSaveClick, primary: true);
        _reload = CreateButton("HostsEditor_Reload", (_, _) => Reload(confirmDiscard: true));
        buttons.Children.Add(_save);
        buttons.Children.Add(_reload);
        buttons.Children.Add(CreateButton("HostsEditor_Close", (_, _) => Close()));
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        PreviewKeyDown += OnPreviewKeyDown;
        Reload(confirmDiscard: false);
    }

    private static Button CreateButton(string translationKey, RoutedEventHandler click, bool primary = false)
    {
        var button = new Button
        {
            Content = TranslationService.Get(translationKey),
            MinWidth = 86,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        var styleKey = primary ? "PrimarySettingsButton" : "SettingsButton";
        if (Application.Current.TryFindResource(styleKey) is Style style)
            button.Style = style;
        button.Click += click;
        return button;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OnSaveClick(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving)
            return;

        var content = _editor.Text;
        SetSaving(true, TranslationService.Get("HostsEditor_Saving"));
        try
        {
            await Task.Run(() => HostsFileStore.SaveElevated(content));
            _loadedText = content;
            SetStatus(TranslationService.Get("HostsEditor_Saved"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus(TranslationService.Get("HostsEditor_SaveCanceled"));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(TranslationService.Get("HostsEditor_SaveFailed"), ex.Message));
        }
        finally
        {
            SetSaving(false, null);
        }
    }

    private void Reload(bool confirmDiscard)
    {
        if (confirmDiscard && !ConfirmDiscard())
            return;

        try
        {
            _loading = true;
            _loadedText = HostsFileStore.Read();
            _editor.Text = _loadedText;
            _editor.CaretIndex = 0;
            SetStatus(TranslationService.Get("HostsEditor_Loaded"));
        }
        catch (Exception ex)
        {
            _loadedText = string.Empty;
            _editor.Text = string.Empty;
            SetStatus(string.Format(TranslationService.Get("HostsEditor_ReadFailed"), ex.Message));
        }
        finally
        {
            _loading = false;
            UpdateStatus();
        }
    }

    private bool IsDirty => !string.Equals(_editor.Text, _loadedText, StringComparison.Ordinal);

    private bool ConfirmDiscard()
    {
        if (!IsDirty)
            return true;

        return MessageBox.Show(
            this,
            TranslationService.Get("HostsEditor_DiscardQuestion"),
            TranslationService.Get("HostsEditor_DiscardTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_saving && !ConfirmDiscard())
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void SetSaving(bool saving, string? status)
    {
        _saving = saving;
        _editor.IsReadOnly = saving;
        _save.IsEnabled = !saving;
        _reload.IsEnabled = !saving;
        if (status != null)
            SetStatus(status);
        else
            UpdateStatus();
    }

    private void UpdateStatus() =>
        SetStatus(TranslationService.Get(IsDirty ? "HostsEditor_Dirty" : "HostsEditor_Loaded"));

    private void SetStatus(string text) => _status.Text = text;
}

internal static class HostsFileStore
{
    internal const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";

    internal static string Read()
    {
        using var reader = new StreamReader(HostsPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void SaveElevated(string content)
    {
        var source = Path.Combine(Path.GetTempPath(), $"lertaro-hosts-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(source, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            RunElevatedWrite(source);
        }
        finally
        {
            try { File.Delete(source); } catch { }
        }
    }

    private static void RunElevatedWrite(string source)
    {
        var escapedSource = source.Replace("'", "''");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $source = '{{escapedSource}}'
            $target = 'C:\Windows\System32\drivers\etc\hosts'
            $backup = "$target.lertaro.bak"
            $staged = "$target.lertaro.tmp"
            try {
                $text = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
                $utf8 = New-Object System.Text.UTF8Encoding($false)
                [IO.File]::WriteAllText($staged, $text, $utf8)
                if ([IO.File]::Exists($target)) {
                    if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
                    [IO.File]::Replace($staged, $target, $backup, $true)
                }
                else {
                    [IO.File]::Move($staged, $target)
                }
            }
            catch {
                if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
                throw
            }
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var start = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the elevated save helper.");
        if (!process.WaitForExit(120_000))
            throw new TimeoutException("The elevated save helper did not finish within 120 seconds.");
        if (process.ExitCode != 0)
            throw new IOException($"The elevated save helper exited with code {process.ExitCode}.");
    }
}
