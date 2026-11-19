#!/usr/bin/env sh
# Disables and removes the HW Widget GNOME Shell extension and its settings.
uuid=hwwidget@solarity.ai
gnome-extensions disable "$uuid" 2>/dev/null || true
rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$uuid" "${XDG_CONFIG_HOME:-$HOME/.config}/hwwidget"
printf '\033[32m✅ HW Widget removed.\033[0m\n'
