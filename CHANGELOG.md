# Changelog

<a name="2.1.0"></a>
## [2.1.0](https://github.com/patricktcoakley/fgvm/releases/tag/v2.1.0) (2026-06-05)

### ✨ Features

* Add `--no-cache` flag to search to force a refresh; migrate `.releases` to `releases.json` to capture more metadata. ([076c79f](https://github.com/patricktcoakley/fgvm/commit/076c79f4c0a9b148876ebe722d426a38ef34b62c))
* Introduce an installation manifest to scaffold future improvements and to allow a shim-based launcher approach instead of relying solely on symlinks; wrap standard IO calls into Result types for cleaner and safer operations; add env var support for configurations; minor cleanup and refactoring. ([3ce783b](https://github.com/patricktcoakley/fgvm/commit/3ce783b6a24566ab2c4318f306fd893c47ff34fa))
* Support shortcuts on Windows, removing the need for Developer Mode to be enabled. ([8c35543](https://github.com/patricktcoakley/fgvm/commit/8c35543e1f5828d9f6c256732fe71d5a56ddcf4b))

### 🐛 Bug Fixes

* On manifest refresh failure the existing manifest will be used. ([e9ec2c8](https://github.com/patricktcoakley/fgvm/commit/e9ec2c8c7721fe86fced61844c80e038cdd2a60e))

<a name="2.0.2"></a>
## [2.0.2](https://github.com/patricktcoakley/fgvm/releases/tag/v2.0.2) (2026-03-01)

### 🐛 Bug Fixes

* **cli:** Centralize installation logic so the experience is the same when called from other commands. ([a1bfea9](https://github.com/patricktcoakley/fgvm/commit/a1bfea9a667866ac83653c1155b2c5cff8025942))

<a name="2.0.1"></a>
## [2.0.1](https://github.com/patricktcoakley/fgvm/releases/tag/v2.0.1) (2026-02-10)

### 🐛 Bug Fixes

* Add explicit check for Windows elevated privileges. ([f9c4636](https://github.com/patricktcoakley/fgvm/commit/f9c4636332fd29578401e3f2f4b01f306dcc9012))
* Add explicit checks for Windows Developer Mode and report warnings instead of hiding them. ([65d9250](https://github.com/patricktcoakley/fgvm/commit/65d92508be6baf18d5965469cb4cf8827f256071))

<a name="2.0.0"></a>
## [2.0.0](https://github.com/patricktcoakley/fgvm/releases/tag/v2.0.0) (2025-11-20)

### ✨ Features

* Support short form commands; update ConsoleAppFramework to unblock hacks around args with params. ([d7e9fb0](https://github.com/patricktcoakley/fgvm/commit/d7e9fb02e9d78d0bca2d52e5355e9e9fbdb44676))
* **install:** Handle checksum verification failure gracefully; introduce more Results; update tests to cover the HTTP clients. ([2023cc0](https://github.com/patricktcoakley/fgvm/commit/2023cc0216f0cd8e9d1d563c637275648960bd6a))
* **logging:** Overhaul logging to use NDJSON; fix removal logic and update removal tests to cover more behavior. ([d7211ca](https://github.com/patricktcoakley/fgvm/commit/d7211ca13bc094fad44feba53941599ad853047b))

### 🐛 Bug Fixes

* **logs:** Update the log file path to avoid colliding with the old logs and ensure the new format is consistent; update default flag; update README to cover remaining topics for migration. ([5fb06e3](https://github.com/patricktcoakley/fgvm/commit/5fb06e3bb92093bb002a6edcef15e4d8a836219c))

### Breaking Changes

* rename the project. ([9743ccf](https://github.com/patricktcoakley/fgvm/commit/9743ccfbb168625b1a24f2bbf56dacf0f555ecca))

<a name="1.4.0"></a>
## [1.4.0](https://github.com/patricktcoakley/gdvm/releases/tag/v1.4.0) (2025-11-13)

### ✨ Features

* Add a GDVM_HOME env var for better testing and user customization. ([4f41fd4](https://github.com/patricktcoakley/gdvm/commit/4f41fd401c30c9de51ab71808416480a912d626b))
* Add JSON output to list, search, and which; update logging for HTTP failures; update e2e tests to fail fast. ([4b9cfcb](https://github.com/patricktcoakley/gdvm/commit/4b9cfcb46c8c61cebc3ad2de25b657a9bb08b94d))
* **HTTP:** Update HTTP calls to write cache so that we don't waste the results; improve error handling. ([b573642](https://github.com/patricktcoakley/gdvm/commit/b5736426c1f6b17ebee59fc264a8fabe3ee52a79))
* **logs:** Add JSON output to logs; refactor logging code and add more testing. ([b7f3122](https://github.com/patricktcoakley/gdvm/commit/b7f312259666ec1de33d2b9422d0091f95752954))

### 🐛 Bug Fixes

* Add backoff logic to the HTTP clients; only update cache when remote fetch happens. ([1d3dc69](https://github.com/patricktcoakley/gdvm/commit/1d3dc69bb23401c0ac705e6680fdf66bea486aa4))
* TuxFamily client should now properly grab checksums from internet archive snapshot for older releases. ([990732d](https://github.com/patricktcoakley/gdvm/commit/990732d120001b4382f9b90ea5de39a8d51b43c2))

<a name="1.3.0"></a>
## [1.3.0](https://github.com/patricktcoakley/gdvm/releases/tag/v1.3.0) (2025-10-16)

### ✨ Features

* Add chronological ordering to search results while keeping the release type ordering for other commands. ([6738471](https://github.com/patricktcoakley/gdvm/commit/6738471576e9829b9ae220d43022d94a984cbb30))

### 🐛 Bug Fixes

* Standardize ordering on other commands; update Zip extensions for path vulnerability; update tests. ([e30e1be](https://github.com/patricktcoakley/gdvm/commit/e30e1be5fec86458ff7a73f39c20b9465d367210))
* **gdvm:** gdvm command should now handle arguments and projects with argument-like names better. ([b285cfd](https://github.com/patricktcoakley/gdvm/commit/b285cfda712d5903c1a0e1a9cc30f92a0379c074))

### ⚡ Performance Improvements

* Minor changes to signficantly improve startup speed; bump dependencies. ([0eab40e](https://github.com/patricktcoakley/gdvm/commit/0eab40e83e98bc7426fbe88072c3f2c89b0c2086))

<a name="1.2.5"></a>
## [1.2.5](https://www.github.com//patricktcoakley/gdvm/releases/tag/v1.2.5) (2025-09-22)

### Bug Fixes

* **install:** remove release pre-filtering causing search/install inconsistency. ([a7e81a5](https://www.github.com//patricktcoakley/gdvm/commit/a7e81a5198539fbfc7538d631baf627209057987))

<a name="1.2.4"></a>
## [1.2.4](https://github.com/patricktcoakley/gdvm/releases/tag/v1.2.4) (2025-08-27)

### 🐛 Bug Fixes

* **install:** Installation command now properly sorts by release type and only dislays versions supported by local machine. ([0edf52a](https://github.com/patricktcoakley/gdvm/commit/0edf52a4b9afbb397c710387eb56058c4a79a89e))

<a name="1.2.3"></a>
## [1.2.3](https://github.com/patricktcoakley/gdvm/releases/tag/v1.2.3) (2025-08-24)

### 🐛 Bug Fixes

* Improve version sorting and argument validation. ([9c11bc3](https://github.com/patricktcoakley/gdvm/commit/9c11bc3e3733c7475d95461074179628a97588e3))

<a name="1.2.2"></a>
## [1.2.2](https://github.com/patricktcoakley/gdvm/releases/tag/v1.2.2) (2025-08-14)

### 🐛 Bug Fixes

* **release:** Fix filtering incorrectly sorting alphabetically instead of by release type ordering. ([8069f7d](https://github.com/patricktcoakley/gdvm/commit/8069f7d48546e0d5f4ceea4db0a79933527b0d41))

<a name="1.2.1"></a>
## [1.2.1](https://www.github.com/patricktcoakley/gdvm/releases/tag/v1.2.1) (2025-07-24)

### 🐛 Bug Fixes

* **which:** Fixed `which` not displaying the currently set versions after last refactor; consolidated error messaging; minor refactoring. ([bd4ae28](https://www.github.com/patricktcoakley/gdvm/commit/bd4ae282bbe02e41fee70bc01b38bdcbb859fde2))

<a name="1.2.0"></a>
## [1.2.0](https://github.com/patricktcoakley/gdvm/releases/tag/v1.2.0) (2025-07-24)

### ✨ Features

* Update the installation status to include download information and updated design; refactor out remaining CLI dependencies from the core. ([11e5849](https://github.com/patricktcoakley/gdvm/commit/11e5849665c8d896cd3d313adab9a83cb512ac0a))

<a name="1.1.2"></a>
## [1.1.2](https://github.com/patricktcoakley/gdvm/releases/tag/v1.1.2) (2025-07-21)

### ✨ Features

* Add GitHub token validation and switch to Versionize. ([9a791ac](https://github.com/patricktcoakley/gdvm/commit/9a791acedcfcb34f9c056910bda5d6fe3b28d165))
* **gdvm:** Add remaining features, refactor existing code, and update README to reflect first stable version: ([b03d245](https://github.com/patricktcoakley/gdvm/commit/b03d245001b8c15da0eb88837b9072e61f5f93b7))
* **local:** Make auto-install default behavior; refactoring to improve testability. ([604b75a](https://github.com/patricktcoakley/gdvm/commit/604b75a697ab078f5320ecd079dd68ca85f37ccc))

### 🐛 Bug Fixes

* Fix having setting the default when only one version is installed; separate CLI and library code; introduce a standardized Result type; remove dead code. ([96c063a](https://github.com/patricktcoakley/gdvm/commit/96c063a190b83a1ae1da9f6d5979443060bae8d1))
* resolve version resolution logic ([76693aa](https://github.com/patricktcoakley/gdvm/commit/76693aa112cb527271a3ad3a366cf69524fdf54c))

<a name="1.1.1"></a>
## [1.1.1](https://github.com/patricktcoakley/gdvm/releases/tag/v1.1.1) (2025-07-08)

### 🐛 Bug Fixes

* resolve version resolution logic ([76693aa](https://github.com/patricktcoakley/gdvm/commit/76693aa112cb527271a3ad3a366cf69524fdf54c))

<a name="1.1.0"></a>
## [1.1.0](https://github.com/patricktcoakley/gdvm/releases/tag/v1.1.0) (2025-07-08)

### ✨ Features

* **local:** Make auto-install default behavior; refactoring to improve testability. ([604b75a](https://github.com/patricktcoakley/gdvm/commit/604b75a697ab078f5320ecd079dd68ca85f37ccc))

<a name="1.0.0"></a>
## [1.0.0](https://github.com/patricktcoakley/gdvm/releases/tag/v1.0.0) (2025-06-17)

### ✨ Features

* **gdvm:** Add remaining features, refactor existing code, and update README to reflect first stable version: ([b03d245](https://github.com/patricktcoakley/gdvm/commit/b03d245001b8c15da0eb88837b9072e61f5f93b7))
  - Add project-aware features to the application, including version management and automatic project launching.
  - Add automatic project file detection and launching.
  - Add local version management with the 'local' command.
  - Implement a version resolution service for contextually detecting which version should be used for various scenarios.
  - Update README to reflect new changes.
  - Improve testability by expanding dependency injection, including the TUI portions of the application.
  - Extract existing functionality into services for better separation of concerns.
  - Add comprehensive test coverage for all new features.