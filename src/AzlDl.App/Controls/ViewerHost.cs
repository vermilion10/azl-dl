using Avalonia;
using Avalonia.Controls;

namespace AzlDl.App.Controls;

// Holds the viewer page. The web view (WebView2 on Windows) is only created the first time there is a page to show,
// so screens that never open the viewer don't start a browser engine.
public class ViewerHost : ContentControl
{
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<ViewerHost, Uri?>(nameof(Source));

    static ViewerHost()
    {
        SourceProperty.Changed.AddClassHandler<ViewerHost>((host, _) => host.Show());
    }

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private void Show()
    {
        if (Source is null)
        {
            return;
        }

        if (Content is NativeWebView web)
        {
            web.Source = Source;
        }
        else
        {
            Content = new NativeWebView { Source = Source };
        }
    }
}
