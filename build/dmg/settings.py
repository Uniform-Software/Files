# Layout of the macOS disk image, read by dmgbuild (https://dmgbuild.readthedocs.io).
# Called by build/release-macos.sh with -D app=<UniFiles.app> -D background=<png> -D icon=<icns>.
# Window size and icon positions match the drawing in build/dmg/make-background.cs.

app = defines["app"]  # noqa: F821 (provided by dmgbuild)

format = "UDZO"
filesystem = "HFS+"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = defines["icon"]  # noqa: F821

background = defines["background"]  # noqa: F821 (background@2x.png next to it is picked up too)
window_rect = ((200, 120), (640, 420))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False

icon_size = 112
text_size = 13
icon_locations = {
    "UniFiles.app": (190, 228),
    "Applications": (450, 228),
}
