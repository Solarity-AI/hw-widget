# Test image: gjs + Pango/cairo + common GNOME fonts.
# BASE=debian:trixie-slim mirrors GNOME Shell 45+; BASE=ubuntu:22.04 is GNOME Shell 42's GJS 1.72 / GLib 2.72.
ARG BASE=debian:trixie-slim
FROM ${BASE}
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends gjs gir1.2-pango-1.0 gir1.2-glib-2.0 fonts-cantarell fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
