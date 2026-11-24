#!/usr/bin/env bash
# Removes Notch for Linux. Your settings, to-dos and memory in ~/.config/notch and ~/.local/share/notch are kept;
# delete those folders too if you want them gone.
pkill -f "python3 -m notch" 2>/dev/null || true
rm -rf "$HOME/.local/share/notch-linux"
rm -f "$HOME/.local/bin/notch" "$HOME/.local/share/applications/notch.desktop" "$HOME/.config/autostart/notch.desktop"
echo "Notch removed. (Settings and memory are still in ~/.config/notch and ~/.local/share/notch.)"
