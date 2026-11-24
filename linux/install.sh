#!/usr/bin/env bash
# Installs Notch for Linux for the current user:
#   bash install.sh
# It installs the few system packages it needs (you'll be asked for your password by sudo), copies the app to
# ~/.local/share/notch-linux, adds "Notch" to your apps menu, starts it at login, and starts it now.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$HOME/.local/share/notch-linux"
BIN="$HOME/.local/bin"

say() { printf '\033[1;35m▸\033[0m %s\n' "$*"; }

say "Installing the packages Notch needs…"
if command -v apt-get >/dev/null; then
  sudo apt-get update -qq
  sudo apt-get install -y python3 python3-gi python3-gi-cairo gir1.2-gtk-3.0 python3-websockets \
       pulseaudio-utils wmctrl libxss1 xdg-user-dirs
  sudo apt-get install -y mpv yt-dlp || say "(mpv / yt-dlp not installed: songs you search will open in the browser)"
elif command -v dnf >/dev/null; then
  sudo dnf install -y python3 python3-gobject gtk3 python3-websockets pulseaudio-utils wmctrl libXScrnSaver xdg-user-dirs
  sudo dnf install -y mpv yt-dlp || say "(mpv / yt-dlp not installed: songs open in the browser)"
elif command -v pacman >/dev/null; then
  sudo pacman -S --needed --noconfirm python python-gobject gtk3 python-websockets libpulse wmctrl libxss xdg-user-dirs
  sudo pacman -S --needed --noconfirm mpv yt-dlp || say "(mpv / yt-dlp not installed: songs open in the browser)"
elif command -v zypper >/dev/null; then
  sudo zypper install -y python3 python3-gobject python3-gobject-cairo typelib-1_0-Gtk-3_0 python3-websockets \
       pulseaudio-utils wmctrl libXss1 xdg-user-dirs
  sudo zypper install -y mpv yt-dlp || say "(mpv / yt-dlp not installed: songs open in the browser)"
else
  say "Unknown package manager: install Python 3 with GObject (GTK 3), python3-websockets, pulseaudio-utils and wmctrl yourself."
fi

say "Copying Notch to $APP…"
pkill -f "python3 -m notch" 2>/dev/null || true
mkdir -p "$APP" "$BIN" "$HOME/.local/share/applications" "$HOME/.config/autostart"
rm -rf "$APP/notch"
cp -r "$SRC/notch" "$SRC/VERSION" "$SRC/install.sh" "$SRC/uninstall.sh" "$APP/"

cat > "$BIN/notch" <<EOF
#!/bin/sh
cd "$APP" && exec python3 -m notch "\$@"
EOF
chmod +x "$BIN/notch" "$APP/install.sh" "$APP/uninstall.sh"

DESKTOP="[Desktop Entry]
Type=Application
Name=Notch
Comment=A living little island at the top of your screen
Exec=$BIN/notch
Icon=$APP/notch/icon.png
Categories=Utility;
StartupNotify=false"
printf '%s\n' "$DESKTOP" > "$HOME/.local/share/applications/notch.desktop"
printf '%s\nX-GNOME-Autostart-enabled=true\nNoDisplay=true\n' "$DESKTOP" > "$HOME/.config/autostart/notch.desktop"

say "Starting Notch…"
nohup "$BIN/notch" >/dev/null 2>&1 &
say "Done! Notch is at the top of your screen. Hover it to open; open Control → Talk setup to add a Gemini key."
