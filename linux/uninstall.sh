#!/usr/bin/env sh
# Disables and removes the HW Widget GNOME Shell extension and its settings.
uuid=hwwidget@solarity.ai
gnome-extensions disable "$uuid" 2>/dev/null || true
# install.sh may have added the uuid to the enabled list itself (when GNOME couldn't enable it yet): take it out.
list=$(gsettings get org.gnome.shell enabled-extensions 2>/dev/null || true)
case "$list" in
  *"'$uuid'"*)
    new=$(printf '%s' "$list" | sed "s/'hwwidget@solarity\.ai'//; s/\[, */[/; s/, *, */, /; s/, *\]/]/")
    [ "$new" = "[]" ] && new="@as []"
    gsettings set org.gnome.shell enabled-extensions "$new" 2>/dev/null || true ;;
esac
rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$uuid" "${XDG_CONFIG_HOME:-$HOME/.config}/hwwidget"
printf '\033[32m✅ HW Widget removed.\033[0m\n'
