import os
import sys

# The island positions itself at the top of the screen and stays above other windows. Wayland doesn't let apps do
# that, so it runs through X11 — under GNOME / KDE Wayland that's XWayland, which every desktop provides.
os.environ.setdefault("GDK_BACKEND", "x11")

from .ui import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
