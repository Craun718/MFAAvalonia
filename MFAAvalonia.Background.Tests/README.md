# Background layout tests

Run with the .NET 10 SDK:

```sh
dotnet test MFAAvalonia.Background.Tests/MFAAvalonia.Background.Tests.csproj -c Release
```

The project compiles the production `ScenicBackground` control directly and uses
Avalonia's headless platform, without starting the automation host or connecting
to a device. It checks complete-image fitting, edge placement, resizing, blur
overscan, clearing, desired size, and bounded brightness. These layout tests do
not replace visual checks of native rendering, settings bindings, and themes.
