# Third-party notices

The complete distribution also includes `Licenses/dependencies.json` and original
license / third-party notice texts collected from the published NuGet dependency
graph, including the .NET runtime, Avalonia, Inter, SkiaSharp and HarfBuzzSharp.
Upstream license texts missing from NuGet packages are tracked in
`packaging/licenses/sources.json` in the source repository. Each plugin carries
its own `NOTICE.md` and `Licenses` directory. These notices do not grant a license
to TractorTool's own code.

## Optional external tools: FFmpeg / ffprobe

The host catalog pins Gyan's FFmpeg 9.0.2 essentials build for Windows x64.
It is downloaded only on user request, separately from the host and plugin DLLs.
Upstream identifies this build as GPLv3; the ZIP's LICENSE and README.txt are retained intact.

- Release / source reference: https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2
- Build information and license: https://www.gyan.dev/ffmpeg/builds/
- Pinned ZIP: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip
- SHA-256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`

## UI dependencies

TractorTool uses FluentAvaloniaUI 3.1.0 (including its bundled symbol font).
Source: https://github.com/amwx/FluentAvalonia
Pinned source revision: 215ee0481e8b0e6d396cbe2a0f33dc791c5646ab
License: MIT

MIT License

Copyright (c) 2025 amwx

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

Watt Toolkit was consulted as a design/architecture reference. Its source,
templates and branded artwork are not included. The TractorTool application
icon is generated from an original imagegen master in Artwork and exported by
scripts/Generate-AppIcon.ps1.


## Microsoft Fluent Emoji

Source: https://github.com/microsoft/fluentui-emoji
Pinned revision: 1ffb34c752ecf5d402f04cfb4b392c77f57c54bc
Included artwork: House, Memo, Toolbox, Gear (original 3D PNGs).
The independent test plugin also includes Puzzle piece with its own license copy.

    MIT License

    Copyright (c) Microsoft Corporation.

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
    SOFTWARE

