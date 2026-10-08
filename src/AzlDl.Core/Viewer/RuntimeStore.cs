namespace AzlDl.Core.Viewer;

public sealed record RuntimeFile(string Name, string Url, string Project, string License);

// The web runtimes the animated viewers need. azl-dl doesn't ship them: two are not open source, so the app fetches
// all of them from their official hosts into <data>/runtimes, and only after the user agrees.
public sealed class RuntimeStore(HttpClient http, string dataFolder)
{
    public static IReadOnlyList<RuntimeFile> Files { get; } =
    [
        new("spine-player.js", "https://raw.githubusercontent.com/EsotericSoftware/spine-runtimes/3.8/spine-ts/build/spine-player.js",
            "Spine Runtimes 3.8", "Spine Runtimes License: each user needs their own Spine Editor license"),
        new("spine-player.css", "https://raw.githubusercontent.com/EsotericSoftware/spine-runtimes/3.8/spine-ts/player/css/spine-player.css",
            "Spine Runtimes 3.8", "Spine Runtimes License"),
        new("live2dcubismcore.min.js", "https://cubism.live2d.com/sdk-web/cubismcore/live2dcubismcore.min.js",
            "Live2D Cubism Core", "Live2D Proprietary Software License"),
        new("pixi.min.js", "https://cdn.jsdelivr.net/npm/pixi.js@6.5.10/dist/browser/pixi.min.js", "PixiJS 6.5.10", "MIT"),
        new("cubism4.min.js", "https://cdn.jsdelivr.net/npm/pixi-live2d-display@0.4.0/dist/cubism4.min.js",
            "pixi-live2d-display 0.4.0", "MIT"),
    ];

    public string Folder => Path.Combine(dataFolder, "runtimes");

    public bool IsInstalled => Files.All(f => File.Exists(Path.Combine(Folder, f.Name)));

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Folder);
        foreach (var file in Files)
        {
            var target = Path.Combine(Folder, file.Name);
            if (File.Exists(target))
            {
                continue;
            }

            var bytes = await http.GetByteArrayAsync(file.Url, cancellationToken);
            var temporary = target + ".part";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
    }
}
