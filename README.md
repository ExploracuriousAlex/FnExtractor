# FnExtractor

A command-line tool to quickly extract specific Fortnite game assets (textures as PNG, data as JSON) using [CUE4Parse](https://github.com/FabianFG/CUE4Parse). The files and folders to extract are defined via simple text-based configuration files.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- [CMake](https://cmake.org/) installed
- [C++ Desktop Development](https://learn.microsoft.com/en-us/cpp/ide/using-the-visual-studio-ide-for-cpp-desktop-development) workload for Visual Studio installed
- Fortnite installed (default path: `C:\Program Files\Epic Games\Fortnite`)

## Setup

1. Clone this repository:
   ```bash
   git clone https://github.com/ExploracuriousAlex/FnExtractor.git
   cd FnExtractor
   ```

2. Clone CUE4Parse into the project directory:
   ```bash
   git clone https://github.com/FabianFG/CUE4Parse.git
   ```
   The resulting folder structure should look like:
   ```
   FnExtractor/
   ├── CUE4Parse/          ← cloned dependency
   ├── Program.cs
   ├── FnExtractor.csproj
   ├── json.txt
   ├── png.txt
   └── ...
   ```

3. Build the project:
   ```bash
   dotnet build
   ```

## Usage

```bash
dotnet run
```

The extractor reads `png.txt` and `json.txt` to determine which assets to export. Results are written to the `./exports` directory, preserving the original folder structure.

### Options

| Flag       | Description                                      |
|------------|--------------------------------------------------|
| `--assets` | Export a full list of all asset paths to `assets.txt` (useful for discovering available paths) |

Example:
```bash
dotnet run -- --assets
```

## Configuration Files

### `png.txt` — Texture exports

Each line specifies a file or folder to export as PNG. Textures (UTexture assets) found in matching packages are decoded and saved.

### `json.txt` — Data exports

Each line specifies a file or folder to export as JSON. Supported formats:
- **Standard packages** — all exports are serialized to JSON
- **`.locres` files** — localization resources are parsed and exported as nested JSON (namespace → key → value)

### Entry format

```
# Lines starting with '#' are comments
# Blank lines are ignored

# A single file (without extension — the tool resolves it automatically):
FortniteGame/Content/Items/ItemCategories

# An entire folder (all files within it and its subfolders):
FortniteGame/Plugins/GameFeatures/SaveTheWorld/Content/Items/Weapons/

# Localization files:
FortniteGame/Content/Localization/Fortnite_locchunk10/en/
```

**Rules:**
- Use forward slashes (`/`) as path separators
- Trailing slashes are optional — entries ending with `/` or matching a folder prefix are treated as folder lookups
- For single files, omit the file extension (e.g. `.uasset`, `.umap`) — the tool resolves the correct file automatically
- Entries are resolved relative to the game's virtual file system (no leading slash)

## Configuration

If your Fortnite installation is in a non-default location, update the `GameDirectory` constant in `Program.cs`:

```csharp
private const string GameDirectory = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks";
```

## License

This project is licensed under the [MIT License](LICENSE). CUE4Parse is licensed under [Apache 2.0](https://github.com/FabianFG/CUE4Parse/blob/master/LICENSE).
