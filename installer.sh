#!/usr/bin/env sh
set -eu

repo="patricktcoakley/fgvm"
version="latest"
minimum_supported_version="2.2.0"
if [ -n "${HOME:-}" ]; then
    default_fgvm_home=$HOME/fgvm
else
    default_fgvm_home=
fi
if [ -n "${FGVM_HOME:-}" ]; then
    fgvm_home=$FGVM_HOME
else
    fgvm_home=$default_fgvm_home
fi
if [ -n "${FGVM_INSTALL_DIR:-}" ]; then
    install_dir=$FGVM_INSTALL_DIR
    install_dir_overridden=1
else
    install_dir=$fgvm_home/bin
    install_dir_overridden=0
fi
modify_path=1
quiet=0

usage() {
    cat <<'EOF'
Usage: installer.sh [OPTIONS]

Options:
  --version VERSION       Release version to install, v2.2.0 or later
  --install-dir PATH      Directory for the fgvm binary, defaults to $FGVM_INSTALL_DIR or $HOME/fgvm/bin
  --fgvm-home PATH        Runtime home for fgvm, defaults to $FGVM_HOME or $HOME/fgvm
  --no-modify-path        Install without changing shell startup files
  -q, --quiet             Suppress informational output
  -h, --help              Show this help
EOF
}

log() {
    if [ "$quiet" -eq 0 ]; then
        printf '%s\n' "$*"
    fi
}

fail() {
    printf 'Error: %s\n' "$*" >&2
    exit 1
}

need_cmd() {
    command -v "$1" >/dev/null 2>&1 || fail "$1 is required to install fgvm."
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            [ "$#" -ge 2 ] || fail "--version requires a value."
            version="$2"
            shift 2
            ;;
        --install-dir)
            [ "$#" -ge 2 ] || fail "--install-dir requires a value."
            install_dir="$2"
            install_dir_overridden=1
            shift 2
            ;;
        --fgvm-home)
            [ "$#" -ge 2 ] || fail "--fgvm-home requires a value."
            fgvm_home="$2"
            if [ "$install_dir_overridden" -eq 0 ]; then
                install_dir=$fgvm_home/bin
            fi
            shift 2
            ;;
        --no-modify-path)
            modify_path=0
            shift
            ;;
        -q|--quiet)
            quiet=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "Unknown option: $1"
            ;;
    esac
done

[ -n "${HOME:-}" ] || fail "HOME must be set."
case "$fgvm_home" in
    /*) ;;
    *) fail "FGVM_HOME must be an absolute path: $fgvm_home" ;;
esac
case "$install_dir" in
    /*) ;;
    *) fail "Install directory must be an absolute path: $install_dir" ;;
esac
fgvm_home_bin=$fgvm_home/bin
case "$fgvm_home_bin" in
    /*) ;;
    *) fail "Default fgvm home bin path must be absolute: $fgvm_home_bin" ;;
esac

need_cmd curl
need_cmd tar
need_cmd uname
need_cmd mktemp
need_cmd sed
need_cmd grep
need_cmd awk

normalize_version_for_comparison() {
    requested="$1"
    case "$requested" in
        v*) requested=${requested#v} ;;
        V*) requested=${requested#V} ;;
    esac

    case "$requested" in
        *-*) requested=${requested%%-*} ;;
    esac

    printf '%s\n' "$requested"
}

major_version() {
    printf '%s\n' "$1" | sed 's/^\([0-9][0-9]*\)\..*$/\1/'
}

minor_version() {
    printf '%s\n' "$1" | sed 's/^[0-9][0-9]*\.\([0-9][0-9]*\)\..*$/\1/'
}

patch_version() {
    printf '%s\n' "$1" | sed 's/^[0-9][0-9]*\.[0-9][0-9]*\.\([0-9][0-9]*\)$/\1/'
}

to_decimal() {
    value=$(printf '%s\n' "$1" | sed 's/^0*//')
    if [ -z "$value" ]; then
        value=0
    fi
    printf '%s\n' "$value"
}

validate_requested_version() {
    [ "$version" = "latest" ] && return 0

    normalized=$(normalize_version_for_comparison "$version")
    if ! printf '%s\n' "$normalized" | grep -Eq '^[0-9]+[.][0-9]+[.][0-9]+$'; then
        fail "Version must be latest or a semantic version such as v$minimum_supported_version."
    fi

    requested_major=$(to_decimal "$(major_version "$normalized")")
    requested_minor=$(to_decimal "$(minor_version "$normalized")")
    requested_patch=$(to_decimal "$(patch_version "$normalized")")

    minimum_major=$(major_version "$minimum_supported_version")
    minimum_minor=$(minor_version "$minimum_supported_version")
    minimum_patch=$(patch_version "$minimum_supported_version")

    if [ "$requested_major" -lt "$minimum_major" ] ||
        { [ "$requested_major" -eq "$minimum_major" ] && [ "$requested_minor" -lt "$minimum_minor" ]; } ||
        { [ "$requested_major" -eq "$minimum_major" ] && [ "$requested_minor" -eq "$minimum_minor" ] && [ "$requested_patch" -lt "$minimum_patch" ]; }; then
        fail "Version overrides only support v$minimum_supported_version or later because older release artifacts use a different layout."
    fi
}

release_tag() {
    case "$version" in
        v*) printf '%s\n' "$version" ;;
        V*) printf 'v%s\n' "${version#V}" ;;
        *) printf 'v%s\n' "$version" ;;
    esac
}

validate_requested_version

platform=$(uname -s)
case "$platform" in
    Darwin) platform="osx" ;;
    Linux) platform="linux" ;;
    *) fail "Unsupported operating system: $platform" ;;
esac

machine=$(uname -m)
case "$machine" in
    x86_64|amd64) architecture="x64" ;;
    arm64|aarch64) architecture="arm64" ;;
    *) fail "Unsupported architecture: $machine" ;;
esac

rid="$platform-$architecture"
archive="fgvm-$rid.tar.gz"
if [ "$version" = "latest" ]; then
    base_url="https://github.com/$repo/releases/latest/download"
else
    tag=$(release_tag)
    base_url="https://github.com/$repo/releases/download/$tag"
fi

target="$install_dir/fgvm"
tmpdir=$(mktemp -d "${TMPDIR:-/tmp}/fgvm-install.XXXXXXXXXX") || fail "Unable to create a temporary directory."
trap 'rm -rf "$tmpdir"' EXIT HUP INT TERM

download() {
    url="$1"
    output_path="$2"

    log "Downloading $url"
    if [ "$quiet" -eq 1 ]; then
        curl -fsSL --retry 3 -o "$output_path" "$url"
    else
        curl -fL --retry 3 -o "$output_path" "$url"
    fi
}

verify_checksum() {
    archive_path="$1"
    checksum_path="$2"

    if command -v sha256sum >/dev/null 2>&1; then
        if ! (cd "$tmpdir" && sha256sum -c "$checksum_path") >/dev/null; then
            fail "Checksum verification failed for $archive_path."
        fi
    elif command -v shasum >/dev/null 2>&1; then
        if ! (cd "$tmpdir" && shasum -a 256 -c "$checksum_path") >/dev/null; then
            fail "Checksum verification failed for $archive_path."
        fi
    else
        fail "sha256sum or shasum is required to verify the release checksum."
    fi

    log "Checksum verified for $archive_path."
}

quote_sh() {
    printf "'"
    printf '%s' "$1" | sed "s/'/'\\\\''/g"
    printf "'"
}

append_unique_path() {
    path_list="$1"
    path_entry="$2"

    case ":$path_list:" in
        *":$path_entry:"*) printf '%s\n' "$path_list" ;;
        *) printf '%s:%s\n' "$path_list" "$path_entry" ;;
    esac
}

path_entries_to_add() {
    path_entries=
    path_entries=$(append_unique_path "$path_entries" "$install_dir")
    path_entries=$(append_unique_path "$path_entries" "$fgvm_home_bin")
    printf '%s\n' "${path_entries#:}"
}

remove_managed_profile_block() {
    profile_path="$1"
    output_path="$2"

    awk '
        /^# >>> fgvm >>>$/ { skip = 1; next }
        /^# <<< fgvm <<<$/{ skip = 0; next }
        !skip { print }
    ' "$profile_path" > "$output_path"
}

select_profile() {
    if [ -n "${FGVM_INSTALL_PROFILE:-}" ]; then
        printf '%s\n' "$FGVM_INSTALL_PROFILE"
        return 0
    fi

    shell_name=${SHELL:-}
    shell_name=${shell_name##*/}
    case "$shell_name" in
        zsh)
            zdotdir=${ZDOTDIR:-$HOME}
            printf '%s\n' "$zdotdir/.zshrc"
            ;;
        bash)
            if [ "$platform" = "osx" ]; then
                printf '%s\n' "$HOME/.bash_profile"
            else
                printf '%s\n' "$HOME/.bashrc"
            fi
            ;;
        sh|dash|ksh)
            printf '%s\n' "$HOME/.profile"
            ;;
        *)
            return 1
            ;;
    esac
}

add_path_to_profile() {
    path_entries=$(path_entries_to_add)

    profile=$(select_profile) || {
        log "Could not detect a shell profile. Add these to PATH manually: $path_entries"
        if [ "$fgvm_home" != "$default_fgvm_home" ]; then
            log "Also persist FGVM_HOME=$fgvm_home before running fgvm."
        fi
        if [ "$install_dir" != "$fgvm_home_bin" ]; then
            log "Also persist FGVM_INSTALL_DIR=$install_dir before re-running the installer."
        fi
        return 0
    }

    profile_dir=${profile%/*}
    if [ "$profile_dir" != "$profile" ]; then
        mkdir -p "$profile_dir"
    fi
    touch "$profile"

    profile_tmp=$tmpdir/profile
    remove_managed_profile_block "$profile" "$profile_tmp"

    path_prefix=
    remaining_paths=$path_entries
    while [ -n "$remaining_paths" ]; do
        path_entry=${remaining_paths%%:*}
        if [ "$remaining_paths" = "$path_entry" ]; then
            remaining_paths=
        else
            remaining_paths=${remaining_paths#*:}
        fi

        quoted_entry=$(quote_sh "$path_entry")
        if [ -z "$path_prefix" ]; then
            path_prefix=$quoted_entry
        else
            path_prefix=$path_prefix:$quoted_entry
        fi
    done

    {
        cat "$profile_tmp"
        printf '\n# >>> fgvm >>>\n'
        if [ "$fgvm_home" != "$default_fgvm_home" ]; then
            quoted_fgvm_home=$(quote_sh "$fgvm_home")
            printf 'export FGVM_HOME=%s\n' "$quoted_fgvm_home"
        fi
        if [ "$install_dir" != "$fgvm_home_bin" ]; then
            quoted_install_dir=$(quote_sh "$install_dir")
            printf 'export FGVM_INSTALL_DIR=%s\n' "$quoted_install_dir"
        fi
        dollar='$'
        printf 'export PATH=%s:%sPATH\n' "$path_prefix" "$dollar"
        printf '# <<< fgvm <<<\n'
    } > "$profile"

    log "Updated fgvm environment in $profile."
}

download "$base_url/$archive" "$tmpdir/$archive"
download "$base_url/$archive.sha256" "$tmpdir/$archive.sha256"

verify_checksum "$tmpdir/$archive" "$tmpdir/$archive.sha256"

mkdir -p "$tmpdir/extract" "$install_dir" "$fgvm_home_bin"
tar -xzf "$tmpdir/$archive" -C "$tmpdir/extract"
[ -f "$tmpdir/extract/fgvm" ] || fail "Release archive did not contain fgvm."

cp "$tmpdir/extract/fgvm" "$target.tmp"
chmod 755 "$target.tmp"
mv "$target.tmp" "$target"
log "Installed fgvm to $target."

if [ "$modify_path" -eq 1 ]; then
    add_path_to_profile
else
    log "Skipped user environment update. Add $(path_entries_to_add) to PATH before running fgvm."
fi

if installed_version=$("$target" --version 2>/dev/null); then
    log "Installed $installed_version."
else
    log "Installed fgvm. Run $target --version to verify the installation."
fi

case ":$PATH:" in
    *":$install_dir:"*) install_dir_on_path=1 ;;
    *) install_dir_on_path=0 ;;
esac
case ":$PATH:" in
    *":$fgvm_home_bin:"*) fgvm_home_bin_on_path=1 ;;
    *) fgvm_home_bin_on_path=0 ;;
esac
case "$install_dir_on_path:$fgvm_home_bin_on_path" in
    1:1) ;;
    *) log "Open a new terminal or add $(path_entries_to_add) to PATH for the current shell." ;;
esac
