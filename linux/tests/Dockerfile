# Test image: gjs + Pango/cairo + common GNOME fonts. Mirrors what GNOME Shell 45+ ships.
FROM debian:trixie-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends gjs gir1.2-pango-1.0 gir1.2-glib-2.0 fonts-cantarell fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
