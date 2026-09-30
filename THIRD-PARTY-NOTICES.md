# Third-party notices

## AsyncImageLoader.Avalonia

This application uses
[AvaloniaUtils/AsyncImageLoader.Avalonia](https://github.com/AvaloniaUtils/AsyncImageLoader.Avalonia),
licensed under the MIT License.

## QQ Music native QR login protocol

The QQ Music native QR login implementation in
`src/ALyricEase/Services/QQMusic/QQMusicQrLoginService.cs` is adapted from
[yakult-green-tea/qq-music-api](https://github.com/yakult-green-tea/qq-music-api),
a maintained fork of Rain120/qq-music-api.

MIT License

Copyright (c) 2019

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

## QQ Music API protocol reference

The QQ Music API client `src/ALyricEase/Services/QQMusic/QQMusicApiClient.cs`
is a C# re-implementation whose upstream protocol (endpoints, `musicu.fcg`
module layout, plain-text signed transport) references
[L-1124/QQMusicApi](https://github.com/L-1124/QQMusicApi),
licensed under the GNU General Public License v3.0.
No source code from the original project is included; this notice is provided
as a courtesy attribution of the protocol reference. The upstream license text
is available at the repository linked above.
