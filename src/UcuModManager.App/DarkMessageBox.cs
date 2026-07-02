using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UcuModManager.App;

public static class DarkMessageBox
{
    public static MessageBoxResult Show(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        return ShowCore(owner, messageBoxText, caption, button, icon);
    }

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        return ShowCore(Application.Current.MainWindow, messageBoxText, caption, button, icon);
    }

    private static MessageBoxResult ShowCore(
        Window? owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var dialog = new DarkMessageDialog(messageBoxText, caption, button, icon)
        {
            Owner = owner?.IsVisible == true ? owner : null
        };

        dialog.ShowDialog();
        return dialog.Result;
    }
}

public sealed class DarkMessageDialog : Window
{
    private readonly MessageBoxButton _buttons;
    private MessageBoxResult _result = MessageBoxResult.None;
    private Button? _defaultButton;

    public DarkMessageDialog(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage icon)
    {
        _buttons = buttons;
        Title = caption;
        Width = 540;
        MinWidth = 440;
        MaxWidth = 760;
        MaxHeight = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brush("#F2F2F2");
        ShowInTaskbar = false;

        Content = BuildContent(message, caption, icon);
        Loaded += (_, _) => _defaultButton?.Focus();
    }

    public MessageBoxResult Result => _result == MessageBoxResult.None
        ? GetCloseResult()
        : _result;

    private UIElement BuildContent(string message, string caption, MessageBoxImage icon)
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

        var titleBar = BuildTitleBar(caption);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = BuildBody(message, icon);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var buttons = BuildButtons();
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        border.Child = root;
        return border;
    }

    private UIElement BuildTitleBar(string caption)
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
            Text = caption,
            Margin = new Thickness(16, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F2F2F2"),
            TextTrimming = TextTrimming.CharacterEllipsis
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
        closeButton.Click += (_, _) => CloseWith(GetCloseResult());
        Grid.SetColumn(closeButton, 1);
        grid.Children.Add(closeButton);

        return grid;
    }

    private UIElement BuildBody(string message, MessageBoxImage icon)
    {
        var grid = new Grid
        {
            Margin = new Thickness(20, 18, 20, 14)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconElement = BuildIcon(icon);
        Grid.SetColumn(iconElement, 0);
        grid.Children.Add(iconElement);

        var messageText = new TextBlock
        {
            Text = message,
            Foreground = Brush("#F2F2F2"),
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };

        var scrollViewer = new ScrollViewer
        {
            Content = messageText,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(14, 1, 0, 0)
        };
        Grid.SetColumn(scrollViewer, 1);
        grid.Children.Add(scrollViewer);

        return grid;
    }

    private UIElement BuildIcon(MessageBoxImage icon)
    {
        var (label, color) = icon switch
        {
            MessageBoxImage.Warning => ("!", "#F2C94C"),
            MessageBoxImage.Error => ("!", "#FF6B6B"),
            MessageBoxImage.Question => ("?", "#58C7B8"),
            MessageBoxImage.Information => ("i", "#58C7B8"),
            _ => ("i", "#A8A8A8")
        };

        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(color),
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brush(color),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private UIElement BuildButtons()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 18)
        };

        foreach (var (text, result, primary, isCancel) in GetButtonDefinitions())
        {
            var button = CreateDialogButton(text, result, primary, isCancel);
            if (primary && _defaultButton is null)
            {
                _defaultButton = button;
            }

            panel.Children.Add(button);
        }

        return panel;
    }

    private IEnumerable<(string Text, MessageBoxResult Result, bool Primary, bool IsCancel)> GetButtonDefinitions()
    {
        return _buttons switch
        {
            MessageBoxButton.OKCancel => new[]
            {
                ("OK", MessageBoxResult.OK, true, false),
                ("Cancel", MessageBoxResult.Cancel, false, true)
            },
            MessageBoxButton.YesNo => new[]
            {
                ("Yes", MessageBoxResult.Yes, true, false),
                ("No", MessageBoxResult.No, false, true)
            },
            MessageBoxButton.YesNoCancel => new[]
            {
                ("Yes", MessageBoxResult.Yes, true, false),
                ("No", MessageBoxResult.No, false, false),
                ("Cancel", MessageBoxResult.Cancel, false, true)
            },
            _ => new[]
            {
                ("OK", MessageBoxResult.OK, true, true)
            }
        };
    }

    private Button CreateDialogButton(
        string text,
        MessageBoxResult result,
        bool primary,
        bool isCancel)
    {
        var button = new Button
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
            IsDefault = primary,
            IsCancel = isCancel,
            Template = CreateButtonTemplate()
        };
        button.Click += (_, _) => CloseWith(result);
        return button;
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

    private MessageBoxResult GetCloseResult()
    {
        return _buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.None
        };
    }

    private void CloseWith(MessageBoxResult result)
    {
        _result = result;
        DialogResult = true;
        Close();
    }

    private static SolidColorBrush Brush(string color)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }
}
