#!/usr/bin/env sh
set -eu

repo="patricktcoakley/fgvm"
version="latest"
minimum_supported_version="2.2.0"
modify_path=1
quiet=0
install_dir_overridden=0

if [ -n "${HOME:-}" ]; then
    default_fgvm_home=$HOME/fgvm
else
    default_fgvm_home=
fi

fgvm_home=${FGVM_HOME:-$default_fgvm_home}
if [ -n "${FGVM_INSTALL_DIR:-}" ]; then
    install_dir=$FGVM_INSTALL_DIR
    install_dir_overridden=1
else
    install_dir=${fgvm_home:+$fgvm_home/bin}
fi

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

parse_args() {
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
}

validate_absolute_path() {
    label="$1"
    path="$2"

    [ -n "$path" ] || fail "$label must be set."
    case "$path" in
        /*) ;;
        *) fail "$label must be an absolute path: $path" ;;
    esac
}

validate_config() {
    [ -n "${HOME:-}" ] || fail "HOME must be set."
    validate_absolute_path "FGVM_HOME" "$fgvm_home"
    validate_absolute_path "Install directory" "$install_dir"

    fgvm_home_bin=$fgvm_home/bin
    validate_absolute_path "Default fgvm home bin path" "$fgvm_home_bin"
}

require_tools() {
    need_cmd curl
    need_cmd tar
    need_cmd uname
    need_cmd mktemp
    need_cmd sed
    need_cmd grep
    need_cmd awk
}

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

resolve_platform() {
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
}

resolve_release_url() {
    if [ "$version" = "latest" ]; then
        base_url="https://github.com/$repo/releases/latest/download"
    else
        base_url="https://github.com/$repo/releases/download/$(release_tag)"
    fi
}

create_temp_dir() {
    tmpdir=$(mktemp -d "${TMPDIR:-/tmp}/fgvm-install.XXXXXXXXXX") || fail "Unable to create a temporary directory."
    trap 'rm -rf "$tmpdir"' EXIT HUP INT TERM
}

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

download_release() {
    download "$base_url/$archive" "$tmpdir/$archive"
    download "$base_url/$archive.sha256" "$tmpdir/$archive.sha256"
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

install_binary() {
    target="$install_dir/fgvm"
    mkdir -p "$tmpdir/extract" "$install_dir" "$fgvm_home_bin"
    tar -xzf "$tmpdir/$archive" -C "$tmpdir/extract"
    [ -f "$tmpdir/extract/fgvm" ] || fail "Release archive did not contain fgvm."

    cp "$tmpdir/extract/fgvm" "$target.tmp"
    chmod 755 "$target.tmp"
    mv "$target.tmp" "$target"
    log "Installed fgvm to $target."
}

quote_sh() {
    printf "'"
    printf '%s' "$1" | sed "s/'/'\\\\''/g"
    printf "'"
}

quote_fish() {
    printf '"'
    printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g; s/\$/\\$/g'
    printf '"'
}

shell_name() {
    name=${SHELL:-}
    printf '%s\n' "${name##*/}"
}

path_entries() {
    printf '%s\n' "$install_dir"
    if [ "$fgvm_home_bin" != "$install_dir" ]; then
        printf '%s\n' "$fgvm_home_bin"
    fi
}

path_entries_colon() {
    if [ "$fgvm_home_bin" = "$install_dir" ]; then
        printf '%s\n' "$install_dir"
    else
        printf '%s:%s\n' "$install_dir" "$fgvm_home_bin"
    fi
}

sh_profiles() {
    if [ -n "${FGVM_INSTALL_PROFILE:-}" ]; then
        printf '%s\n' "$FGVM_INSTALL_PROFILE"
        return 0
    fi

    case "$(shell_name)" in
        zsh)
            printf '%s\n' "${ZDOTDIR:-$HOME}/.zshrc"
            ;;
        bash)
            if [ "$platform" = "osx" ]; then
                printf '%s\n%s\n' "$HOME/.bash_profile" "$HOME/.bashrc"
            else
                printf '%s\n%s\n' "$HOME/.bashrc" "$HOME/.profile"
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

remove_managed_profile_block() {
    profile_path="$1"
    output_path="$2"

    awk '
        /^# >>> fgvm >>>$/ { skip = 1; next }
        /^# <<< fgvm <<<$/{ skip = 0; next }
        !skip { print }
    ' "$profile_path" > "$output_path"
}

write_sh_exports() {
    if [ "$fgvm_home" != "$default_fgvm_home" ]; then
        printf 'export FGVM_HOME=%s\n' "$(quote_sh "$fgvm_home")"
    fi

    if [ "$install_dir" != "$fgvm_home_bin" ]; then
        printf 'export FGVM_INSTALL_DIR=%s\n' "$(quote_sh "$install_dir")"
    fi
}

write_sh_path_prepend() {
    printf '_fgvm_path=%s\n' "$(quote_sh "$1")"
    printf 'case ":$PATH:" in\n'
    printf '    *":$_fgvm_path:"*) ;;\n'
    printf '    *) PATH="$_fgvm_path${PATH:+:$PATH}" ;;\n'
    printf 'esac\n'
}

write_sh_env_script() {
    env_script="$1"
    env_dir=${env_script%/*}
    [ "$env_dir" = "$env_script" ] || mkdir -p "$env_dir"

    {
        printf '#!/bin/sh\n'
        printf '# Generated by fgvm. Do not edit.\n'
        write_sh_exports
        [ "$fgvm_home_bin" = "$install_dir" ] || write_sh_path_prepend "$fgvm_home_bin"
        write_sh_path_prepend "$install_dir"
        printf 'export PATH\n'
        printf 'unset _fgvm_path\n'
    } > "$env_script"
}

write_profile_source() {
    profile="$1"
    env_script="$2"
    profile_dir=${profile%/*}
    [ "$profile_dir" = "$profile" ] || mkdir -p "$profile_dir"
    touch "$profile"

    profile_tmp=$tmpdir/profile
    remove_managed_profile_block "$profile" "$profile_tmp"

    {
        cat "$profile_tmp"
        printf '\n# >>> fgvm >>>\n'
        printf '. %s\n' "$(quote_sh "$env_script")"
        printf '# <<< fgvm <<<\n'
    } > "$profile"

    log "Updated fgvm environment in $profile."
}

write_fish_exports() {
    if [ "$fgvm_home" != "$default_fgvm_home" ]; then
        printf 'set -gx FGVM_HOME %s\n' "$(quote_fish "$fgvm_home")"
    fi

    if [ "$install_dir" != "$fgvm_home_bin" ]; then
        printf 'set -gx FGVM_INSTALL_DIR %s\n' "$(quote_fish "$install_dir")"
    fi
}

write_fish_path_prepend() {
    quoted_entry=$(quote_fish "$1")
    printf 'if not contains %s $PATH\n' "$quoted_entry"
    printf '    set -gx PATH %s $PATH\n' "$quoted_entry"
    printf 'end\n'
}

write_fish_env_script() {
    fish_conf_dir=${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d
    fish_env_script=$fish_conf_dir/fgvm.env.fish
    mkdir -p "$fish_conf_dir"

    {
        printf '# Generated by fgvm. Do not edit.\n'
        write_fish_exports
        [ "$fgvm_home_bin" = "$install_dir" ] || write_fish_path_prepend "$fgvm_home_bin"
        write_fish_path_prepend "$install_dir"
    } > "$fish_env_script"

    log "Updated fgvm fish environment in $fish_env_script."
}

write_github_path() {
    [ -n "${GITHUB_PATH:-}" ] || return 0
    path_entries | while IFS= read -r path_entry; do
        printf '%s\n' "$path_entry" >> "$GITHUB_PATH"
    done
    log "Updated GitHub Actions PATH in $GITHUB_PATH."
}

write_manual_instructions() {
    log "Could not detect a shell profile. Add these to PATH manually: $(path_entries_colon)"
    if [ "$fgvm_home" != "$default_fgvm_home" ]; then
        log "Also persist FGVM_HOME=$fgvm_home before running fgvm."
    fi
    if [ "$install_dir" != "$fgvm_home_bin" ]; then
        log "Also persist FGVM_INSTALL_DIR=$install_dir before re-running the installer."
    fi
}

install_sh_environment() {
    env_script=$fgvm_home/env
    write_sh_env_script "$env_script"

    sh_profiles | while IFS= read -r profile; do
        [ -n "$profile" ] || continue
        write_profile_source "$profile" "$env_script"
    done
}

install_shell_environment() {
    write_github_path

    if [ -z "${FGVM_INSTALL_PROFILE:-}" ] && [ "$(shell_name)" = "fish" ]; then
        write_fish_env_script
        return 0
    fi

    if sh_profiles >/dev/null; then
        install_sh_environment
    else
        write_manual_instructions
    fi
}

update_shell_environment() {
    if [ "$modify_path" -eq 1 ]; then
        install_shell_environment
    else
        log "Skipped user environment update. Add $(path_entries_colon) to PATH before running fgvm."
    fi
}

verify_install() {
    if installed_version=$("$target" --version 2>/dev/null); then
        log "Installed $installed_version."
    else
        log "Installed fgvm. Run $target --version to verify the installation."
    fi
}

path_contains() {
    case ":$PATH:" in
        *":$1:"*) return 0 ;;
        *) return 1 ;;
    esac
}

print_path_notice() {
    if path_contains "$install_dir" && path_contains "$fgvm_home_bin"; then
        return 0
    fi

    log "Open a new terminal or add $(path_entries_colon) to PATH for the current shell."
}

main() {
    parse_args "$@"
    validate_config
    require_tools
    validate_requested_version
    resolve_platform
    resolve_release_url
    create_temp_dir
    download_release
    verify_checksum "$tmpdir/$archive" "$tmpdir/$archive.sha256"
    install_binary
    update_shell_environment
    verify_install
    print_path_notice
}

main "$@"
