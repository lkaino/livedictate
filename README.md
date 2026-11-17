<img src="assets/icon.svg" width="64" height="64" alt="">

# livedictate

Created in [T3 Code](https://t3.codes).

## Contents

- [`TeamsCaptionsSaver/`](TeamsCaptionsSaver/) — Windows app that saves Microsoft Teams live captions
  (with speaker names) and records the meeting audio (mic + speaker loopback) as Ogg Opus. Based on
  [mufeezahmad/TeamsCaptionsSaver](https://github.com/mufeezahmad/TeamsCaptionsSaver) `e4a5ff5`
  (MIT), imported with `git subtree`. Build with the .NET 10 SDK:

  ```powershell
  cd TeamsCaptionsSaver
  dotnet publish TeamsCaptionsSaver.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Users\<you>\Apps\TeamsCaptionsSaver
  ```
