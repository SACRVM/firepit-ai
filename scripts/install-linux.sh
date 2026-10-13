#!/usr/bin/env bash
# Installs Firepit for the current user, or updates an installation:
#   - firepitd            → ~/.local/opt/firepit (framework-dependent)
#   - firepit-mcp         → a .NET global tool in ~/.dotnet/tools
#   - firepit.service     → ~/.config/systemd/user, enabled and (re)started
# Nothing outside $HOME is touched. Run from anywhere in the repo.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${FIREPIT_PREFIX:-$HOME/.local/opt/firepit}"
units="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
nupkg="$repo/bin/nupkg"

echo "firepitd    → $prefix"
dotnet publish "$repo/src/Firepit.Daemon/Firepit.Daemon.csproj" \
    -c Release -o "$prefix" --nologo -v quiet

echo "firepit-mcp → ~/.dotnet/tools"
rm -rf "$nupkg"
dotnet pack "$repo/tools/firepit-mcp/Firepit.Mcp.Bridge.csproj" -c Release --nologo -v quiet
# Reinstall rather than update: a rebuild of the same version would be
# skipped as "already installed".
dotnet tool uninstall --global firepit-mcp >/dev/null 2>&1 || true
dotnet tool install --global firepit-mcp --add-source "$nupkg" --ignore-failed-sources

echo "firepit.service → $units"
mkdir -p "$units"
install -m 644 "$repo/packaging/systemd/firepit.service" "$units/firepit.service"
install -m 644 "$repo/packaging/systemd/firepit@.service" "$units/firepit@.service"
systemctl --user daemon-reload
systemctl --user enable firepit.service
systemctl --user restart firepit.service

sleep 2
systemctl --user --no-pager --lines=0 status firepit.service || true
echo
echo "Logs: journalctl --user -u firepit -f"
