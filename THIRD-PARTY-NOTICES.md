# Third-party notices

The complete distribution also includes `Licenses/dependencies.json` and original
license / third-party notice texts collected from the published NuGet dependency
graph, including the .NET runtime, Avalonia, Inter, SkiaSharp and HarfBuzzSharp.
Upstream license texts missing from NuGet packages are tracked in
`packaging/licenses/sources.json` in the source repository. Each plugin carries
its own `NOTICE.md` and `Licenses` directory. These notices do not grant a license
to TractorTool's own code.

## Windows installer build tool

The optional Windows installer is built with Inno Setup 6.7.3 by Jordan Russell
and Martijn Laan. The compiler is a build-time tool, not an application dependency.
Original license: https://github.com/jrsoftware/issrc/blob/is-6_7_3/license.txt
The source repository retains that text in `packaging/licenses/InnoSetup.txt`.
The Simplified Chinese installer translation is from the official repository,
revision `6ef32198ef1f7b7b375cd4b6b90896c2a58eb4c2`,
`Files/Languages/ChineseSimplified.isl`; author attribution is preserved in the
translation file and the installer's About dialog.

## Optional external tools

External tool versions, upstream releases, archive hashes and license metadata
are declared by each plugin, not by a host catalog. The host resolves the declared
GitHub Release asset and downloads it after user confirmation, separately from
the host and plugin DLLs. Each plugin's NOTICE.md identifies its external tools;
original license and notice files remain in the downloaded package.

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

