# Third-party notices

LeagueAkari-MyGo is an independent lightweight edition based on
[LeagueAkari](https://github.com/LeagueAkari/LeagueAkari) 1.5.0-rabi.2,
source revision `1f76e8e2`. It is not an official upstream release.

LeagueAkari is copyright (c) 2026 Hanxven and licensed under MIT. Its original
license is retained in `desktop/LICENSE` and distributed as `LeagueAkari-LICENSE.txt`.
The shared Vue UI, business contracts, native bindings and upstream resources
remain under `desktop/`. The Go backend replaces the Electron runtime for the
current desktop release. Historical prototype code remains under `app/`.

The host uses [MyGo](https://github.com/egoist/mygo) v0.2.10, copyright (c) 2026
MyGo contributors, under MIT. Its complete license is distributed as
`MyGo-LICENSE.txt`. Windows supplies the Microsoft Edge WebView2 runtime.
Other dependencies retain their respective licenses.

The experimental WinUI 3 host uses Microsoft Windows App SDK 1.8 and .NET 10.
The portable acceptance package includes their self-contained runtime files and
redistribution notices. The backend is built from the existing Go services; its
dedicated WinUI entrypoint does not create a MyGo window, load the Vue UI or
start WebView2. Legacy MyGo code and its MIT dependency are retained in the
repository and acknowledged in the acceptance package.

Native champion search includes a compressed default dictionary and an adapted
no-tone segmenter from [pinyin-pro](https://github.com/zh-lx/pinyin-pro) 3.28.1,
copyright (c) 2022-present zh-lx, under MIT. The dictionary is generated from
the installed upstream package by `scripts/generate-native-pinyin.cjs`; it is
used directly by .NET and does not require Node.js or a browser at runtime.
The original MIT terms for this included component follow:

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

League of Legends, its artwork and trademarks belong to Riot Games and their
respective rights holders. Their inclusion does not imply endorsement.

Thank you to Hanxven and the LeagueAkari contributors for the original project,
and to egoist and the MyGo contributors for the lightweight desktop framework.
