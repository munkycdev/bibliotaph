# Native Mac spike

Throwaway code that answers one question before any change to the Windows app: does a native AppKit app in C# feel
right on macOS 27, and does the toolchain work? It is not part of the solution or CI, and nothing in `src/` depends on it.

## What it shows

- A window with AppKit's own sidebar and inspector split view items and a unified toolbar. macOS draws all of them as
  Liquid Glass; the spike draws no glass itself.
- The web library's real folders and files, read the way a connected PC reads them: the Mac connects to
  bibliotaph.online as a device (`/api/devices/connect`), then reads the shelf (`/api/pc/library`) and covers
  (`/api/pc/books/{hash}/reading`, `/api/pc/covers/{name}`). It never sends a sync file or results, so it changes nothing
  in the library.
- `Shared/` holds code with no AppKit in it, standing in for view models a shared Presentation project would hold.
  `App/` holds the AppKit views and `Ui.Watch`, the small binder that replaces WPF's data binding.

Titles come from file names, since the shelf carries files rather than catalogued books. The device key is kept in the
login keychain. The device shows in the web library under Settings › Devices; **Bibliotaph › Disconnect from
bibliotaph.online** removes it.

## Running it

Needs Apple Silicon, full Xcode (not only the Command Line Tools), the .NET 11 SDK and the macOS workload:

```
sudo xcode-select -s /Applications/Xcode.app
dotnet workload install macos
cd spikes/mac-native/BibliotaphMac
dotnet build
open bin/Debug/net11.0-macos/osx-arm64/Bibliotaph.app
```

A local build is signed ad hoc and runs without notarization.
