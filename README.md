# Evidence

Screenshots and measurements behind each PR's manual checks, kept off `main` so the code's
history stays free of images. One folder per PR (`pr-10/`); the PR's comments link these files
at a fixed commit.

The pictures come from a harness run on Stefan's PC against the real app and the repo's
`tools/TestWindow`. Each shows only the test window's own pixels (`PrintWindow`) at its real
position on an outline of the monitor; the rest of the screen isn't captured. The `*-log.json`
files hold the measured style, outer rect and client rect for every picture.
