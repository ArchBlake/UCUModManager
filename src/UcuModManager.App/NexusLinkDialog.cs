using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UcuModManager.App;

public sealed record NexusLinkDialogInitial(
    string ModName,
    string DefaultGameDomain,
    string InstalledVersion,
    UcuModManager.Core.Mods.ModSourceInfo? Source);

public sealed record NexusLinkDialogResult(
    string GameDomain,
    int ModId,
    int? FileId,
    string? FileVersion);

public sealed class NexusLinkDialog : Window
{
    private readonly TextBox _urlTextBox = new();
    private readonly TextBox _gameDomainTextBox = new();
    private readonly TextBox _modIdTextBox = new();
    private readonly TextBox _fileIdTextBox = new();
    private readonly TextBox _versionTextBox = new();
    private readonly TextBlock _statusText = new();

    public NexusLinkDialog(NexusLinkDialogInitial initial)
    {
        Title = "Link Nexus";
        Width = 560;
        MinWidth = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brush("#F2F2F2");
        ShowInTaskbar = false;

        Content = BuildContent(initial);
        Loaded += (_, _) => _urlTextBox.Focus();
    }

    public NexusLinkDialogResult? Result { get; private set; }

    private UIElement BuildContent(NexusLinkDialogInitial initial)
    {
        var border = new Border
        {
            Background = Brush("#181818"),
            BorderBrush = Brush("#303030"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            SnapsToDevicePixels = true
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = BuildTitleBar(initial.ModName);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = BuildBody(initial);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = BuildButtons();
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        border.Child = root;
        return border;
    }

    private UIElement BuildTitleBar(string modName)
    {
        var grid = new Grid
        {
            Background = Brush("#141414")
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        var title = new TextBlock
        {
            Text = $"Link Nexus - {modName}",
            Margin = new Thickness(16, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F2F2F2"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var closeButton = CreateButton("x", false);
        closeButton.Width = 46;
        closeButton.Height = 34;
        closeButton.Margin = new Thickness(0, 4, 6, 4);
        closeButton.Click += (_, _) => Close();
        Grid.SetColumn(closeButton, 1);
        grid.Children.Add(closeButton);

        return grid;
    }

    private UIElement BuildBody(NexusLinkDialogInitial initial)
    {
        var grid = new Grid
        {
            Margin = new Thickness(20, 18, 20, 14)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        ConfigureTextBox(_urlTextBox);
        ConfigureTextBox(_gameDomainTextBox);
        ConfigureTextBox(_modIdTextBox);
        ConfigureTextBox(_fileIdTextBox);
        ConfigureTextBox(_versionTextBox);

        _gameDomainTextBox.Text = string.IsNullOrWhiteSpace(initial.Source?.GameDomain)
            ? initial.DefaultGameDomain
            : initial.Source!.GameDomain!;
        _modIdTextBox.Text = initial.Source?.ModId?.ToString() ?? string.Empty;
        _fileIdTextBox.Text = initial.Source?.FileId?.ToString() ?? string.Empty;
        _versionTextBox.Text = !string.IsNullOrWhiteSpace(initial.Source?.FileVersion)
            ? initial.Source!.FileVersion!
            : initial.InstalledVersion.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : initial.InstalledVersion;
        _urlTextBox.Text = BuildInitialUrl(_gameDomainTextBox.Text, _modIdTextBox.Text);

        AddRow(grid, 0, "Nexus URL", _urlTextBox);
        AddRow(grid, 1, "Game domain", _gameDomainTextBox);
        AddRow(grid, 2, "Mod ID", _modIdTextBox);
        AddRow(grid, 3, "File ID", _fileIdTextBox);
        AddRow(grid, 4, "Version", _versionTextBox);

        _statusText.Text = "Paste a Nexus URL and Save, or fill fields manually. Version is optional.";
        _statusText.Foreground = Brush("#A8A8A8");
        _statusText.TextWrapping = TextWrapping.Wrap;
        _statusText.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_statusText, 5);
        Grid.SetColumn(_statusText, 1);
        grid.Children.Add(_statusText);

        return grid;
    }

    private UIElement BuildButtons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 18)
        };

        var parseButton = CreateButton("Parse URL", false);
        parseButton.Click += (_, _) => ParseUrl();
        panel.Children.Add(parseButton);

        var saveButton = CreateButton("Save", true);
        saveButton.IsDefault = true;
        saveButton.Click += (_, _) => Save();
        panel.Children.Add(saveButton);

        var cancelButton = CreateButton("Cancel", false);
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) => Close();
        panel.Children.Add(cancelButton);

        return panel;
    }

    private static void AddRow(Grid grid, int row, string label, TextBox textBox)
    {
        var labelText = new TextBlock
        {
            Text = label,
            Foreground = Brush("#A8A8A8"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8)
        };
        Grid.SetRow(labelText, row);
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        textBox.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(textBox, row);
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);
    }

    private void ParseUrl()
    {
        if (!TryApplyParsedUrl(showFailure: true))
        {
            return;
        }

        SetStatus("Nexus URL parsed.", "#58C7B8");
    }

    private bool TryApplyParsedUrl(bool showFailure)
    {
        var parsed = TryParseNexusLink(_urlTextBox.Text);
        if (parsed is null)
        {
            if (showFailure)
            {
                SetStatus("Could not parse this Nexus URL.", "#FF6B6B");
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(parsed.GameDomain))
        {
            _gameDomainTextBox.Text = parsed.GameDomain;
        }

        _modIdTextBox.Text = parsed.ModId.ToString();
        if (parsed.FileId is not null)
        {
            _fileIdTextBox.Text = parsed.FileId.Value.ToString();
        }

        return true;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_modIdTextBox.Text)
            || string.IsNullOrWhiteSpace(_gameDomainTextBox.Text))
        {
            TryApplyParsedUrl(showFailure: false);
        }

        var gameDomain = _gameDomainTextBox.Text.Trim();
        if (!Regex.IsMatch(gameDomain, "^[A-Za-z0-9_-]+$"))
        {
            SetStatus("Game domain must look like scavprototype.", "#FF6B6B");
            return;
        }

        if (!int.TryParse(_modIdTextBox.Text.Trim(), out var modId) || modId <= 0)
        {
            if (TryApplyParsedUrl(showFailure: false)
                && int.TryParse(_modIdTextBox.Text.Trim(), out modId)
                && modId > 0)
            {
                gameDomain = _gameDomainTextBox.Text.Trim();
            }
            else
            {
                SetStatus("Paste a Nexus URL or enter a positive Mod ID. Version can stay empty.", "#FF6B6B");
                return;
            }
        }

        if (!Regex.IsMatch(gameDomain, "^[A-Za-z0-9_-]+$"))
        {
            SetStatus("Game domain must look like scavprototype.", "#FF6B6B");
            return;
        }

        int? fileId = null;
        var fileIdText = _fileIdTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(fileIdText))
        {
            if (!int.TryParse(fileIdText, out var parsedFileId) || parsedFileId <= 0)
            {
                SetStatus("File ID must be empty or a positive number.", "#FF6B6B");
                return;
            }

            fileId = parsedFileId;
        }

        var version = _versionTextBox.Text.Trim();
        Result = new NexusLinkDialogResult(
            gameDomain,
            modId,
            fileId,
            string.IsNullOrWhiteSpace(version) ? null : version);
        DialogResult = true;
        Close();
    }

    private static ParsedNexusLink? TryParseNexusLink(string value)
    {
        var input = value.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (int.TryParse(input, out var rawModId) && rawModId > 0)
        {
            return new ParsedNexusLink(null, rawModId, null);
        }

        var candidate = input;
        if (!candidate.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase)
                ? "https://" + candidate
                : "https://www.nexusmods.com/" + candidate.TrimStart('/');
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var modsIndex = Array.FindIndex(segments, segment => segment.Equals("mods", StringComparison.OrdinalIgnoreCase));
            if (modsIndex > 0
                && modsIndex + 1 < segments.Length
                && int.TryParse(segments[modsIndex + 1], out var modId)
                && modId > 0)
            {
                return new ParsedNexusLink(
                    segments[modsIndex - 1],
                    modId,
                    TryGetQueryInt(uri.Query, "file_id") ?? TryGetQueryInt(uri.Query, "fileid"));
            }
        }

        var match = Regex.Match(input, @"(?i)(?<domain>[A-Za-z0-9_-]+)/mods/(?<modId>\d+)");
        return match.Success
            ? new ParsedNexusLink(
                match.Groups["domain"].Value,
                int.Parse(match.Groups["modId"].Value),
                null)
            : null;
    }

    private static int? TryGetQueryInt(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && Uri.UnescapeDataString(pair[0]).Equals(key, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(Uri.UnescapeDataString(pair[1]), out var value)
                && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static string BuildInitialUrl(string gameDomain, string modId)
    {
        return !string.IsNullOrWhiteSpace(gameDomain) && !string.IsNullOrWhiteSpace(modId)
            ? $"https://www.nexusmods.com/{gameDomain}/mods/{modId}?tab=files"
            : string.Empty;
    }

    private void SetStatus(string text, string color)
    {
        _statusText.Text = text;
        _statusText.Foreground = Brush(color);
    }

    private static void ConfigureTextBox(TextBox textBox)
    {
        textBox.Height = 30;
        textBox.Padding = new Thickness(8, 4, 8, 4);
        textBox.Background = Brush("#202020");
        textBox.Foreground = Brush("#F2F2F2");
        textBox.BorderBrush = Brush("#303030");
        textBox.CaretBrush = Brush("#F2F2F2");
        textBox.SelectionBrush = Brush("#58C7B8");
        textBox.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static Button CreateButton(string text, bool primary)
    {
        return new Button
        {
            Content = text,
            MinWidth = 86,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Background = primary ? Brush("#58C7B8") : Brush("#202020"),
            Foreground = primary ? Brush("#061210") : Brush("#F2F2F2"),
            BorderBrush = primary ? Brush("#58C7B8") : Brush("#303030"),
            BorderThickness = new Thickness(1),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = Cursors.Hand,
            Template = CreateButtonTemplate()
        };
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = border
        };

        var mouseOver = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        mouseOver.Setters.Add(new Setter(Border.OpacityProperty, 0.86, "ButtonBorder"));
        template.Triggers.Add(mouseOver);

        var pressed = new Trigger
        {
            Property = Button.IsPressedProperty,
            Value = true
        };
        pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.72, "ButtonBorder"));
        template.Triggers.Add(pressed);

        return template;
    }

    private static SolidColorBrush Brush(string color)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }

    private sealed record ParsedNexusLink(string? GameDomain, int ModId, int? FileId);
}
