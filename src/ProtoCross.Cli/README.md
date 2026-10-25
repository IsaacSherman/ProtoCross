# ProtoCross

ProtoCross compiles methods written against protobuf messages into C# and C++. You write each method
once, in a small language with defined, tested cross-language semantics, and declare its tests beside
it. ProtoCross generates the method and the tests for every backend, and `protocross test` builds and
runs them in each.

```
extend InvoiceItem {
    fn line_total_cents() -> int64 {
        return quantity * unit_price_cents;
    }
}
```

## Install

```
dotnet tool install --global ProtoCross
```

To pin the version a repository builds with, install it as a local tool and run it as
`dotnet protocross`:

```
dotnet new tool-manifest
dotnet tool install ProtoCross
```

On .NET 10 you can also run it without installing it: `dnx ProtoCross --help`.

## What it needs

- **.NET 10 or newer.** Installing a tool needs the .NET SDK.
- **protoc**, to read your schemas. It is found through `PROTOCROSS_PROTOC`, then `PATH`, then a
  `Grpc.Tools` package in your NuGet cache. [Install protoc](https://protobuf.dev/installation/).
- **For `protocross test`:** `dotnet` for C#, and CMake, a C++ compiler and protobuf C++ with its
  CMake package for C++.

## Use

```
protocross pricing.pcross -I protos -o generated
protocross billing/billing.pcproj -o generated
protocross test billing/billing.pcproj -o generated
protocross --help
```

## More

- [The README](https://github.com/IsaacSherman/ProtoCross#readme): the language, projects,
  configuration and the generated API.
- [The specification](https://github.com/IsaacSherman/ProtoCross/blob/main/ProtoCross_Spec/README.md).
- The VS Code extension, for diagnostics, completion and navigation as you type: on the
  [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=isaacsherman.protocross)
  and [Open VSX](https://open-vsx.org/extension/isaacsherman/protocross).
