# Evidence

Screenshots and measurements behind each PR's manual checks, kept off `main` so the code's
history stays free of images. One folder per PR (`pr-10/`); the PR's comments link these files
at a fixed commit.

The pictures come from a harness run on Stefan's PC against the real app and the repo's
`tools/TestWindow`. Each shows only the test window's own pixels (`PrintWindow`) at its real
position on an outline of the monitor; the rest of the screen isn't captured. The `*-log.json`
files hold the measured style, outer rect and client rect for every picture.

Since PR #12 (the black backdrop), a picture can also be a screen capture of the monitor
(`CopyFromScreen`), taken only while the app's backdrop covers all of it, so it shows nothing but
the game, black, and windows the harness itself opened. When the taskbar is above the backdrop,
its strip is painted over and labelled "taskbar (masked; measured above the backdrop)"; any other
program's window above the backdrop is painted over too, and the log says so. Black shares are
measured before masking, and nothing unmasked is saved. Each caption and log entry says which
way its picture was made.
