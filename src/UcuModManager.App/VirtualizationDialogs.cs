using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Media;

namespace UcuModManager.App;

public sealed class VirtualizationIntroDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.No;

    public VirtualizationIntroDialog(VirtualizationIntroState state)
    {
        Title = "Virtualized Launch";
        Width = 560;
        MinWidth = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brush("#F2F2F2");
        ShowInTaskbar = false;

        Content = BuildContent(state);
    }

    public MessageBoxResult Result => _result;

    private UIElement BuildContent(VirtualizationIntroState state)
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

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new StackPanel
        {
            Margin = new Thickness(22, 18, 22, 16)
        };
        body.Children.Add(new TextBlock
        {
            Text = "Virtualized Launch is enabled for alpha testing.",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F2F2F2"),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = "It is recommended by default, but please report bugs and strange behavior while testing.",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            LineHeight = 19,
            Foreground = Brush("#D8D8D8"),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brush("#2A2115"),
            BorderBrush = Brush("#F2A33A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new TextBlock
            {
                Text = "Important: use a clean game folder and install BepInEx first. The safest way is to install BepInEx through UCU ModManager.",
                Foreground = Brush("#F2A33A"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            }
        });
        body.Children.Add(new TextBlock
        {
            Text = state.IsSupported
                ? $"Link check: {state.LinkMode} is available."
                : $"Link check failed: {state.Message}",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 12,
            Foreground = state.IsSupported ? Brush("#58C7B8") : Brush("#F2C94C"),
            TextWrapping = TextWrapping.Wrap
        });

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(22, 0, 22, 18)
        };
        buttons.Children.Add(CreateButton("Use Virtualization", MessageBoxResult.Yes, primary: true, isCancel: false));
        buttons.Children.Add(CreateButton("Disable", MessageBoxResult.No, primary: false, isCancel: true));
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        border.Child = root;
        return border;
    }

    private UIElement BuildTitleBar()
    {
        var grid = new Grid { Background = Brush("#141414") };
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
            Text = "Virtualized Launch",
            Margin = new Thickness(16, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F2F2F2")
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var closeButton = new Button
        {
            Content = "x",
            Width = 46,
            Height = 34,
            Margin = new Thickness(0, 4, 6, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brush("#F2F2F2"),
            Cursor = Cursors.Hand,
            Template = CreateButtonTemplate()
        };
        closeButton.Click += (_, _) => CloseWith(MessageBoxResult.No);
        Grid.SetColumn(closeButton, 1);
        grid.Children.Add(closeButton);

        return grid;
    }

    private Button CreateButton(string text, MessageBoxResult result, bool primary, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 104,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Background = primary ? Brush("#58C7B8") : Brush("#202020"),
            Foreground = primary ? Brush("#061210") : Brush("#F2F2F2"),
            BorderBrush = primary ? Brush("#58C7B8") : Brush("#303030"),
            BorderThickness = new Thickness(1),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Cursor = Cursors.Hand,
            IsDefault = primary,
            IsCancel = isCancel,
            Template = CreateButtonTemplate()
        };
        button.Click += (_, _) => CloseWith(result);
        return button;
    }

    private void CloseWith(MessageBoxResult result)
    {
        _result = result;
        DialogResult = true;
        Close();
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonBorder";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));

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
}

public sealed class VirtualLaunchProgressDialog : Window
{
    private readonly TextBlock _statusText = new();
    private readonly string _heading;
    private readonly string _initialStatus;

    public VirtualLaunchProgressDialog(
        string title = "Virtualized Launch",
        string heading = "Preparing virtualized profile",
        string initialStatus = "Starting...")
    {
        _heading = heading;
        _initialStatus = initialStatus;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brush("#F2F2F2");
        ShowInTaskbar = false;
        Content = BuildContent();
    }

    public void SetStatus(string text)
    {
        _statusText.Text = text;
    }

    private UIElement BuildContent()
    {
        var border = new Border
        {
            Background = Brush("#181818"),
            BorderBrush = Brush("#303030"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(22, 18, 22, 20),
            SnapsToDevicePixels = true
        };

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = _heading,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F2F2F2")
        });

        _statusText.Text = _initialStatus;
        _statusText.Margin = new Thickness(0, 8, 0, 12);
        _statusText.Foreground = Brush("#A8A8A8");
        _statusText.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_statusText);

        root.Children.Add(BuildProgressIndicator());

        border.Child = root;
        return border;
    }

    private static UIElement BuildProgressIndicator()
    {
        var track = new Border
        {
            Height = 8,
            Background = Brush("#202020"),
            BorderBrush = Brush("#303030"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };

        var lane = new Grid
        {
            ClipToBounds = true
        };

        lane.Children.Add(new Border
        {
            Height = 2,
            Margin = new Thickness(3, 2, 3, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brush("#263B38"),
            CornerRadius = new CornerRadius(1)
        });

        var transform = new TranslateTransform();
        var bar = new Rectangle
        {
            Width = 120,
            Height = 8,
            RadiusX = 4,
            RadiusY = 4,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Brush("#2F7E75").Color, 0.0),
                    new(Brush("#58C7B8").Color, 0.48),
                    new(Brush("#8AE8DD").Color, 1.0)
                },
                new Point(0, 0),
                new Point(1, 0)),
            Effect = null,
            RenderTransform = transform
        };
        lane.Children.Add(bar);

        track.Child = lane;
        track.Loaded += (_, _) =>
        {
            var targetWidth = Math.Max(track.ActualWidth, 360);
            var animation = new DoubleAnimation
            {
                From = -bar.Width,
                To = targetWidth,
                Duration = TimeSpan.FromSeconds(1.15),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        };

        return track;
    }

    private static SolidColorBrush Brush(string color)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }
}

public sealed record VirtualizationIntroState(
    bool IsSupported,
    string LinkMode,
    string Message);
