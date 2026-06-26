# fgvm

**fgvm**, a ***friendly*** **Godot version manager**.

> [!IMPORTANT]
> This project was previously known as `gdvm`, but as of 2.0 has now been renamed to `fgvm`. Most users won't be significantly impacted,
> but some changes were breaking and will require users to switch over to `fgvm`; please see [this section](#migrating-from-gdvm) for information on how to migrate.

## Introduction

fgvm is a friendly Godot version manager that lets users install and manage multiple versions of Godot with ease. It uses a hybrid CLI/TUI design, meaning that in certain places where it makes sense
it will prompt you to let you select what you're looking for instead of having to pass in confusing arguments, as well as support for [passing it unstructured queries](#usage) to help find the
appropriate version based on your input, like `4 dev` or `latest`. It's released as a self-contained native executable for Windows, macOS, and Linux that can be run without installing the .NET runtime,
either by putting it somewhere on your `PATH` or, preferably, using a [package manager](#package-managers).

## Features

- **Version Management**: Easily manage multiple Godot installations side-by-side, allowing you to try out the latest versions or keep older versions for compatibility testing, including Godot 1.0 to
  the latest development builds, including both standard and .NET builds.
- **Hybrid CLI/TUI Interface**: Simple command-line interface with interactive TUI prompts for easy navigation and selection when you don't specify arguments.
- **Flexible Query System**: Powerful query system for finding and installing versions using keywords like `latest`, `4 mono`, `3.3 rc`, etc.
- **Project Aware**: Lock a project to a specific Godot version using a `.fgvm-version` file in the project directory. `fgvm local` can automatically detect a compatible version from `project.godot`
  or let you manually choose one, and will prompt to install missing versions when needed. `fgvm godot` uses `.fgvm-version` when present, otherwise falls back to the global default, and can launch the
  current project directly from the terminal.
- **Smart Argument Handling**: Detection of arguments passed to Godot that contextually switch to an attached mode when necessary to display terminal output.
- **CI-Ready**: Suitable for remote installations, CI/CD pipelines, WSL, and containerized environments with its single self-contained native executable.

## Installation

> [!NOTE]
> On **Windows**, fgvm creates an optional `Godot.url` shortcut to the selected version for GUI launch compatibility.
> You can still install, remove, set, and launch versions with `fgvm godot` if that shortcut cannot be created.
>
> In addition, PowerShell, the default shell for Windows, doesn't support the emojis out of the box. To fix this, you simply need to update the `$PROFILE`/profile.ps1:
> ```powershell 
> '[console]::InputEncoding = [console]::OutputEncoding = [System.Text.UTF8Encoding]::new()' | Add-Content -Path $PROFILE
> ```
>
> Also, if you are using `cmd`, you can also try the beta unicode support by going to Region in the control panel, going to Administrative, clicking Change system locale, and checking the Beta:
> Use Unicode UTF-8 for worldwide language support checkbox. You will have to restart your computer, but it should enable emoji support there as well.


### Package Managers

The recommended way to install fgvm is through a package manager, which will make it easier to keep up to date and manage your installations:

#### Homebrew (macOS/Linux)

If you're on macOS or Linux, you can install fgvm using [Homebrew](https://brew.sh) by running the following commands:

```shell
brew tap patricktcoakley/formulae
brew trust patricktcoakley/formulae
brew install fgvm
```

Homebrew 5 requires third-party taps to be explicitly trusted before their formulae can be installed; `brew trust` marks the tap as trusted so `brew install fgvm` succeeds. See the [`brew trust` documentation](https://docs.brew.sh/Manpage#trust-options-target) for more details.

Note that you may periodically need to run `brew update` if any changes are applied to the formula.

Alternatively, macOS users can [download the release with `curl`](#macos-command-line-installation). This avoids the browser-added quarantine attribute that can cause Gatekeeper warnings for the
non-notarized binaries.

#### mise (macOS/Linux)

[mise](https://mise.jdx.dev/) can install fgvm directly from the GitHub release artifacts using its GitHub backend:

```shell
mise use -g github:patricktcoakley/fgvm@2.2.0
fgvm --version
```

For now, use the full `github:patricktcoakley/fgvm` tool name. The shorter `mise use -g fgvm` form will only work after fgvm is added to mise's registry.

#### Scoop (Windows)

If you're on Windows, you can install fgvm using [Scoop](https://scoop.sh) by running the following commands:

```powershell
scoop bucket add patricktcoakley https://github.com/patricktcoakley/scoop-bucket
scoop install patricktcoakley/fgvm
```

### fgvmup (Currently Windows only)

There is also an **experimental** tool called `fgvmup` that can manage your installations on **Windows** using a PowerShell script. I've only done preliminary testing and am open to feedback, but be
aware things there may be issues. To try it out, you can do the following:

```powershell
irm https://raw.githubusercontent.com/patricktcoakley/fgvm/main/installer.ps1 | iex
```

which will install the latest version and add fgvmup, fgvm, and the Godot alias directories to your PATH automatically. fgvmup
can handle installation, upgrade, and deletion of the fgvm tool, but it's a WIP and may change or be integrated into the main application in the future.

Usage:

- `install` [`--quiet`] [`--version VERSION`] [`--force`] installs fgvmup and fgvm, with the optional arguments for quiet output, a specific version, or forcing an installation.
- `uninstall` removes **everything**, including fgvm, fgvmup, and all Godot installations.
- `upgrade` just reinstalls everything and will likely be removed in the future unless I can think of a use case.

As of now I really only created it as a proof-of-concept but could expand it later in the future. If there is interest I will also consider a macOS/Linux version of this tool using a traditional shell
script.

### Pre-built Binaries

If you don't want to use a package manager, download the archive for your platform from the [latest release](https://github.com/patricktcoakley/fgvm/releases/latest):

| Platform | Architecture | Archive |
| --- | --- | --- |
| Windows | x64 | [`fgvm-win-x64.zip`](https://github.com/patricktcoakley/fgvm/releases/latest/download/fgvm-win-x64.zip) |
| macOS | Intel x64 | [`fgvm-osx-x64.tar.gz`](https://github.com/patricktcoakley/fgvm/releases/latest/download/fgvm-osx-x64.tar.gz) |
| macOS | Apple Silicon ARM64 | [`fgvm-osx-arm64.tar.gz`](https://github.com/patricktcoakley/fgvm/releases/latest/download/fgvm-osx-arm64.tar.gz) |
| Linux | x64 | [`fgvm-linux-x64.tar.gz`](https://github.com/patricktcoakley/fgvm/releases/latest/download/fgvm-linux-x64.tar.gz) |
| Linux | ARM64 | [`fgvm-linux-arm64.tar.gz`](https://github.com/patricktcoakley/fgvm/releases/latest/download/fgvm-linux-arm64.tar.gz) |

Each archive has a matching `.sha256` file on the release. Windows uses ZIP files; macOS and Linux use tarballs.
Extract the executable into a directory on your `PATH`; on macOS and Linux, `chmod +x fgvm` is only needed if your filesystem or extraction tool strips executable permissions.

The Linux binaries require glibc and do not support musl-based distributions such as Alpine Linux.

#### macOS command-line installation

The macOS binaries require macOS 12 or later and are not currently notarized. Downloads made through a browser may be quarantined by Gatekeeper. Downloading with `curl` does not apply the browser
quarantine attribute, so you can verify and run the release directly:

```shell
case "$(uname -m)" in
  arm64) archive=fgvm-osx-arm64.tar.gz ;;
  x86_64) archive=fgvm-osx-x64.tar.gz ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

base_url=https://github.com/patricktcoakley/fgvm/releases/latest/download
curl -fLO "$base_url/$archive"
curl -fLO "$base_url/$archive.sha256"
shasum -a 256 -c "$archive.sha256"
tar -xzf "$archive"
./fgvm --version
```

After confirming it runs, move `fgvm` into a directory on your `PATH`.

#### Downloading with GitHub CLI

The [GitHub CLI](https://cli.github.com/) can download both the archive and its checksum from the latest release:

```shell
archive=fgvm-osx-arm64.tar.gz # Replace with the archive for your platform.
gh release download --repo patricktcoakley/fgvm \
  --pattern "$archive" \
  --pattern "$archive.sha256"
```

Verify the downloaded archive before extracting it:

```shell
# macOS
shasum -a 256 -c "$archive.sha256"

# Linux
sha256sum -c "$archive.sha256"
```

Extract macOS and Linux archives with `tar`:

```shell
tar -xzf "$archive"
./fgvm --version
```

On Windows, verify the expected hash from `fgvm-win-x64.zip.sha256` against the downloaded archive:

```powershell
$expected = (Get-Content .\fgvm-win-x64.zip.sha256).Split()[0]
$actual = (Get-FileHash .\fgvm-win-x64.zip -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "Checksum verification failed." }
```

### Build From Source

See [Build](#build) for instructions on how to build fgvm from source.

## Usage

### Getting Started

Install the latest stable standard build, set it as the global default, and launch Godot:

```shell
fgvm install latest --default
fgvm godot
```

fgvm downloads and installs Godot into folders inside of `~/fgvm/` for macOS and Linux, and `$env:USERPROFILE\fgvm\` for Windows. You can customize this location using the `FGVM_HOME` environment variable (see [Environment Variables](#environment-variables)).
New installations are stored under `installations/<VERSION>-<TYPE>-<RUNTIME>/<TARGET>/`, and fgvm tracks them in `installations.json`. For example, a 4.3 stable .NET install on Linux x64 is tracked as `installations/4.3-stable-mono/linux.x86_64/`.

By default, fgvm records the selected version in `installations.json`. It also creates a stable PATH shim at `bin/godot` on macOS/Linux or `bin/godot.cmd` on Windows, and best-effort creates a root symlink named `Godot` on Linux, `Godot.app` on macOS, or a `Godot.url` shortcut on Windows for GUI launch compatibility.
You can run `fgvm godot -i` to pick another installation to launch, or use `fgvm set` to pick the version you want to launch by default.

Godot installation availability is separate from fgvm's own release matrix. fgvm selects Godot artifacts for the detected operating system and CPU architecture, so older Godot releases may not be
available on newer targets, particularly macOS ARM64. Downloading an artifact for a different target is not currently supported.

### Commands

All of this is also available in the `--help` section of the app:

```shell
fgvm --help
```

but here is a detailed summary of the available commands:

> **Note:** Many commands support short-form aliases for faster usage (e.g., `fgvm i` for `fgvm install`, `fgvm g` for `fgvm godot`).

- `fgvm list` or `fgvm l` [`--json`] will list locally installed Godot versions. Use `--json` to output in JSON format.
- `fgvm install` or `fgvm i` `[<...strings>]` [`--default|-D`] will prompt the user to install a version if no arguments are supplied, or will
  try to find the closest matching version based on the query, defaulting to "stable" if no other release type is supplied.
  It will automatically set the installed version as the default if it's the first installation. Use `--default` (or `-D`) to explicitly set the installed version as the default regardless of whether other versions are already installed.
    - Queries:
        - `latest` or `latest standard` will install the latest stable, and `latest mono` will install the latest .NET stable.
        - `4 mono` will grab the latest stable 4.x .NET release, `3.3 rc` will grab the latest rc of 3.3 standard, `1` would take the last stable version `1`, and so on.
    - Examples:
        - `fgvm install 4.3` - Install 4.3 stable
        - `fgvm install 4.3 mono` - Install 4.3 stable mono
        - `fgvm i latest --default` - Install latest stable standard and set as default
- `fgvm godot` or `fgvm g` runs the appropriate Godot version, or with the `--interactive` or `-i` flag, will prompt the user to launch an installed version. When run in a project directory with a `.fgvm-version`
  file, it will use that project-specific version. If no `.fgvm-version` file exists, it will use the global default version. The command will automatically detect and launch the project if a
  `project.godot` file is found.
    - Once a version is installed, it will launch the editor with the project directly from the terminal.
    - Optionally, pass in arguments to the Godot executable directly using the `--args` parameter, such as `fgvm godot --args "--headless"` or `fgvm godot --args "--version"`. Multiple arguments should be
      passed as a quoted string, such as `--args "--headless -v"`.
    - Use `--project` or `-P` with explicit arguments to add the detected project path, such as `fgvm godot -P --args "--dump-extension-api --quit"`.
    - Use the `--attached` or `-a` flag to force Godot connected to the terminal for output; by default, Godot runs in detached mode and will launch in a separate instance. Using an argument detection
      system, certain arguments (like `--version`, `--help`, `--headless`) automatically trigger this mode since they would otherwise be useless without printing to standard out.
    - The command will only read existing `.fgvm-version` files for version selection, and does not create or modify version files. Use `fgvm local` to manage `.fgvm-version` files.
- `fgvm set [<...strings>]` prompts the user to set an installed version of Godot if no arguments are supplied, or will
  try to find the closest matching version based on the query, including release type (`stable`) and version (`4`, `4.4`), or an exact match (`4.4.1-stable-mono`).
- `fgvm local [<...strings>]` sets the Godot version for the current project by creating or updating a `.fgvm-version` file in the current directory. If no `.fgvm-version` file
  exists and no arguments are provided, it will automatically detect the project version from `project.godot` and install the most recent compatible version if not already installed.
    - If a list of arguments are provided, it will find the best matching version based on the query (including runtime preferences like `mono` or `standard`) and install it if necessary.
- `fgvm which` [`--json`] displays the executable path for the effective Godot installation in the current directory: `.fgvm-version` first, then the global default. Use `--json` to output in JSON format.
- `fgvm remove` or `fgvm r` `[<...strings>]` prompts the user to select multiple installations to delete, or optionally takes a query to filter down to specific versions to delete. If there is only one match, it
  will delete it directly. If there are multiple matches, it will prompt the user to select which ones to delete.
    - For example, if you wanted to list all of the `4.y.z` versions to remove, you could just do `fgvm r 4` to list all of the 4 major releases. However, if you remove a specific version, like
      `4.4.1-stable-mono`, it will just delete that version directly. Deleting the currently set version will unset it and you will need to set a new one.
- `fgvm logs` [`--level|-l <string>`] [`--message|-m <string>`] [`--json`] displays all of the logs, or optionally takes a level or message filter. Use `--json` to output in JSON format.
- `fgvm search` or `fgvm s` `[<...strings>]` [`--json|-j`] [`--no-cache|-F`] takes an optional query to search available Godot versions. Use `--json` or `-j` to output in JSON format,
  and `--no-cache` or `-F` to force a remote refresh instead of using the local release cache.
    - Queries:
        - `4` would filter all 4.x releases, including "stable", "dev", etc.
        - `4.2-rc` would only list the `4.2` `rc` releases, but `4.2 rc` would list all `4.2.x` releases with the `rc` release type, including `4.2.2-rc3`

### Project Version Management

fgvm supports project-specific version management through `.fgvm-version` files. Here's how it works:

#### Setting up a project version:

```bash
# Navigate to your project directory
cd my-godot-project

# Option 1: Auto-detect version from project.godot
fgvm local                    # Detects version from project.godot, creates .fgvm-version

# Option 2: Explicitly set a version
fgvm local 4.3 mono          # Creates .fgvm-version with 4.3-stable-mono
```

#### Using project versions:

```bash
# In a project directory with .fgvm-version file
fgvm godot                    # Uses version from .fgvm-version
# Or use short form
fgvm g                        # Same as above

# In a project directory without .fgvm-version file
fgvm godot                    # Uses global default version

# In any directory
fgvm godot -i                 # Interactive selection from installed versions
fgvm g -i                     # Same as above
```

#### Workflow:

1. **`fgvm local`** - Creates/updates `.fgvm-version` file for project-specific version management
2. **`fgvm godot`** (or `fgvm g`) - Respects `.fgvm-version` file if present, otherwise uses global default
3. **`fgvm set`** - Sets the global default version used when no `.fgvm-version` exists

### Environment Variables

- **`FGVM_HOME`**: Customize the installation directory for fgvm. By default, fgvm uses `~/fgvm/` (macOS/Linux) or `$env:USERPROFILE\fgvm\` (Windows). Setting this variable allows you to use a different location:

  macOS/Linux:
    ```bash
    # Temporary (current session only)
    export FGVM_HOME=/custom/path/fgvm
    fgvm list  # Will use /custom/path/fgvm/ directly

    # Persistent (add to ~/.bashrc, ~/.zshrc, or ~/.profile)
    echo 'export FGVM_HOME=/custom/path/fgvm' >> ~/.bashrc
    ```

  Windows:
    ```powershell
    # Temporary (current session only)
    $env:FGVM_HOME = "C:\custom\path\fgvm"
    fgvm list  # Will use C:\custom\path\fgvm\ directly

    # Persistent (for current user)
    [System.Environment]::SetEnvironmentVariable('FGVM_HOME', 'C:\custom\path\fgvm', 'User')
    ```

  This is particularly useful for testing, CI/CD environments, or keeping your Godot installations on a separate storage device for backup purposes.

## Development

### Build

Building the project requires the .NET 10 SDK. Publishing the Native AOT executable also requires the native compiler toolchain for the target platform: Visual Studio with the Desktop development
with C++ workload on Windows, Xcode Command Line Tools on macOS, or Clang and the required development libraries on Linux. See the [.NET Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites)
for platform-specific setup.

Run `dotnet run --project Fgvm.Cli -- <command> [args]` during development, or publish a self-contained release binary for a specific runtime identifier:

```shell
git clone https://github.com/patricktcoakley/fgvm.git
cd fgvm
dotnet restore
dotnet publish Fgvm.Cli/Fgvm.Cli.csproj -c Release -r <RID>
```

Use one of the release RIDs: `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, or `linux-arm64`. The executable is written to `Fgvm.Cli/bin/Release/net10.0/<RID>/publish/`.

This repo also includes an optional [mise](https://mise.jdx.dev/) setup for installing the expected .NET SDK and running common development tasks:

```shell
mise install
mise run restore
mise run build
mise run test
```

### Test

The mise tasks are the recommended way to run tests because they install the expected tool versions and prepare the integration and end-to-end fixtures:

```shell
mise install
mise run test
mise run test:e2e
```

The more specific tasks are also available:

```shell
mise run test:unit
mise run test:integration
mise run test:e2e:detailed
```

Without mise, install the .NET 10 SDK and PowerShell 7, make sure `dotnet` and `pwsh` are on your `PATH`, and run the equivalent preparation and test commands directly:

```shell
dotnet restore

dotnet test --configuration Release Fgvm.Tests/Fgvm.Tests.csproj

dotnet run Fgvm.Tests.Integration/Fixtures/PublishCli.cs
dotnet test --configuration Release Fgvm.Tests.Integration/Fgvm.Tests.Integration.csproj

dotnet run e2e/fixtures/BuildFixtures.cs
dotnet run Fgvm.Tests.Integration/Fixtures/PublishCli.cs --output e2e/.cli
pwsh -NoLogo -NoProfile -File e2e/run.ps1 -Parallel
```

### Contributing

This project uses [Conventional Commits](https://www.conventionalcommits.org/) for commit messages and [Versionize](https://github.com/versionize/versionize) for automated versioning and changelog
generation.

When making changes:

1. Use conventional commit format: `type(scope): description`.
2. Supported types: `feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `chore`, `ci`, `build`.
3. The changelog is automatically generated from these commits.

Example:

```shell
git commit -m "feat(environment): Added support for OpenBSD."
```

Also please make sure to run `mise run format` before committing to ensure code style consistency.

See: https://github.com/patricktcoakley/fgvm

## Roadmap

- Possibly consider adding multi-select and multi-query to installations so that you could bulk-install multiple versions.
- I currently have [fgvmup](#fgvmup-currently-windows-only) for Windows, and it would make sense to port that script to bash for macOS and Linux support, allowing users to more easily install fgvm
  without having to rely on a package manager, but at the cost of extra maintenance and overhead.

## Migrating from gdvm

If you were using this project in the past then you'll know it used to be called `gdvm`. Prior to this project's creation and after, there have been several other projects with similar goals using the same name. 

In an effort to differentiate this project I decided to change the name to stand out, and am also using it as an opportunity to implement some breaking changes due to some recent updates in the libraries I am using to write this tool.

What this means for you:
- `gdvm` and `fgvm` are mostly the same workflow but there were minor changes to the commands that are breaking, so consult the updated documentation if you get stuck
- If you are using a package manager (the recommended way to install), you will have to remove the `gdvm` package and install `fgvm`
  - Homebrew users: `brew update && brew uninstall gdvm && brew install fgvm`
  - Scoop users: `scoop update && scoop uninstall gdvm && scoop install fgvm`
- If you want to keep your current installations, you can copy the existing `gdvm` directory to `fgvm`, which will preserve everything. Here are some one-liners that copy them over and delete the gdvm folder:
  - macOS & Linux users: `mkdir -p ~/fgvm && cp -r ~/gdvm/* ~/fgvm/ && rm -rf ~/gdvm`
  - Windows users: `mkdir -Force $env:USERPROFILE\fgvm ; cp -r $env:USERPROFILE\gdvm\* $env:USERPROFILE\fgvm\ ; rm -r -Force $env:USERPROFILE\gdvm`
  - `gdvmup` is now called `fgvmup`. If you were using the old `gdvmup` installer, run `gdvmup uninstall` first, which removes everything, then follow the [installation instructions](#installation) above. Be sure to copy over your existing installations using the above commands if you want to keep them.
