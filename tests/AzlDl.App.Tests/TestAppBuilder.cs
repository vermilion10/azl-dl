using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(AzlDl.App.Tests.TestAppBuilder))]

namespace AzlDl.App.Tests;

// Runs the real App headlessly with Skia, so rendered frames can be captured as images.
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
