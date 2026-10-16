# Third-party notices

azl-dl includes or depends on the software and fonts listed below. Each is the work of its own authors and is used under the license named here. The full license texts follow the lists.

## Libraries

| Component | Version | License | Copyright |
|---|---|---|---|
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (core, desktop, Skia, HarfBuzz, Fluent theme, Win32, X11, FreeDesktop, native) | 12.1.3 | MIT | The AvaloniaUI Project |
| [Avalonia.Controls.WebView](https://avaloniaui.net/) | 12.1.0 | MIT | AvaloniaUI OÜ |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (Avalonia.Angle.Windows.Natives) | 2.1.27548 | BSD 3-Clause | The ANGLE Project Authors |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.4 | MIT | Microsoft Corporation |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | 8.3.1.3 | MIT | Microsoft Corporation |
| [MicroCom.Runtime](https://github.com/kekekeks/MicroCom) | 0.11.6 | MIT | Nikita Tsukanov |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) | 0.94.1 | MIT | Tom Deseyn |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | .NET Foundation and Contributors |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | 3.0.0-preview.7 | MIT | .NET Foundation and Contributors |
| [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) and AssetsTools.NET.Texture | 3.0.5, 3.0.2 | MIT | nesrak1 |
| [AssetRipper.TextureDecoder](https://github.com/AssetRipper/TextureDecoder) | 1.3.0 | MIT | ds5678 |
| [VGAudio](https://github.com/Thealexbarney/VGAudio) | 2.2.1 | MIT | Alex Barney |
| [StbImageWriteSharp](https://github.com/StbSharp/StbImageWriteSharp) | 1.16.7 | Public domain | StbSharp |

The .NET runtime libraries (System.\*) are © .NET Foundation and Contributors, under the MIT license.

SkiaSharp and HarfBuzzSharp ship the native Skia and HarfBuzz libraries, which carry their own notices. These are listed in [SkiaSharp's third-party notices](https://github.com/mono/SkiaSharp/blob/main/THIRD-PARTY-NOTICES.txt).

## Fonts

The app bundles these fonts, all under the SIL Open Font License 1.1. Each font's license file is next to it in `src/AzlDl.App/Assets/Fonts`.

| Font | Copyright |
|---|---|
| [Big Shoulders Display](https://github.com/xotypeco/big_shoulders) | Copyright 2019 The Big Shoulders Project Authors |
| [Chakra Petch](https://github.com/m4rc1e/Chakra-Petch) | Copyright 2018 The Chakra Petch Project Authors |
| [IBM Plex Sans and IBM Plex Mono](https://github.com/IBM/plex) | Copyright 2017 IBM Corp., with Reserved Font Name "Plex" |
| [M PLUS 1p](https://mplusfonts.github.io/) | Copyright 2016 The M+ Project Authors |
| [Newsreader](https://github.com/productiontype/Newsreader) | Copyright 2020 The Newsreader Project Authors |
| [Public Sans](https://github.com/uswds/public-sans) | Copyright 2015 The Public Sans Project Authors |

## Downloaded at your request

azl-dl doesn't include the web runtimes behind its animated viewers. When you open a viewer for the first time, the app lists them with their licenses and downloads them from their official hosts only if you agree. They remain under their owners' licenses:

| Runtime | License |
|---|---|
| Spine Runtimes 3.8 (Esoteric Software) | Spine Runtimes License |
| Live2D Cubism Core (Live2D Inc.) | Live2D Proprietary Software License |
| PixiJS 6.5.10 | MIT |
| pixi-live2d-display 0.4.0 | MIT |

## Game content

azl-dl contains no game content. Art, audio, text and data are downloaded from the game's servers to your computer when you use the tools. They belong to Manjuu, Yongshi and Yostar.

## Acknowledgements

The key for the game's CRI audio comes from the public key list of [vgmstream](https://github.com/vgmstream/vgmstream).

## License texts

### MIT License

Applies to the components listed above as MIT, each with its own copyright line.

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### BSD 3-Clause License (ANGLE)

```
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

### SIL Open Font License 1.1

The full text is included with each font in `src/AzlDl.App/Assets/Fonts` and is published at https://openfontlicense.org.
