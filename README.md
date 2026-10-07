# ChatterFix

[![CI](https://github.com/ardazeybek-dev/chatterfix/actions/workflows/ci.yml/badge.svg)](https://github.com/ardazeybek-dev/chatterfix/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4.svg)](#requirements)

A worn mouse switch bounces as its contacts meet, so one physical click reaches
Windows as two. ChatterFix sits between the mouse and every application, measures
the interval between a release and the next press, and drops the events no hand
could have produced.

It never generates a click of its own. It only removes events the hardware should
not have sent.

## The two faults it repairs

**Chatter — one click arrives as two.** The second press lands a few milliseconds
after the release, far faster than a finger can move. ChatterFix swallows it, and
swallows its matching release as well: an application that receives a release with
no press treats the button as stuck down.

**Drop — a held button lets go by itself.** The same worn contact can break while
the button is still held, which Windows reports as a release immediately followed
by a press. Measured on a worn switch, these breaks last 15–30 ms. ChatterFix holds
every release back for as long as the chatter threshold. If a press arrives inside
that window the contact merely broke, so both events are dropped and the hold
continues unbroken — a drag survives, and a phantom click right after a real one is
folded into it. A badly worn contact breaks for much longer, up to 160 ms, which is
as long as the pause inside a double click. What tells them apart is the press
before: a double click never starts with a long press, so once a press has lasted
150 ms its release is held for the longer **hold repair** window instead. Otherwise the release is sent on. A drop that
outlasts the window looks like chatter when the contact returns, so that press is
swallowed at first — but if it is still held 30 ms later it was the hold resuming,
not a bounce, and ChatterFix sends it after all.

## How it decides

The rule is a speed limit no human hand can reach:

| What | Gap between release and next press |
|---|---|
| Ordinary clicking | 150–400 ms |
| Deliberate double click | 80–200 ms |
| Jitter clicking (~13 per second) | ~75 ms |
| Fast clicking (~30 per second) | ~15 ms |
| **Chatter (hardware fault)** | **1–25 ms** |

The measurement is taken from the **release**, not from the previous press. A press
that follows a two-second hold is seconds away from the last press, so a
press-to-press comparison would miss a fault there entirely.

## Profiles

One threshold cannot serve every situation. On the desktop nobody clicks twice
within 35 ms, so a wide threshold is free of risk. In a game the same hand may
reach thirty clicks a second, where 35 ms would start eating real clicks.

So thresholds follow whichever application has focus:

| Profile | Applies to | Threshold | Drop repair | Hold repair |
|---|---|---|---|---|
| Desktop | everything not listed elsewhere | 35 ms | 35 ms | 200 ms |
| Fast clicking | `javaw`, `java`, `Minecraft`, `LunarClient`, … | 12 ms | 8 ms | 200 ms |

Profiles are editable in **Settings**; the one with no process names is the
fallback and cannot be removed.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build

## Install

```bash
git clone https://github.com/ardazeybek-dev/chatterfix.git
cd chatterfix
dotnet build -c Release
```

Run the tray application:

```bash
dotnet run --project src/ChatterFix.App -c Release
```

Or publish a standalone folder and run `ChatterFix.exe` from it:

```bash
dotnet publish src/ChatterFix.App -c Release -o publish/app
```

The icon appears in the notification area: green while protecting, grey while
paused. Double-click it for live statistics, right-click for settings and for
**Start with Windows**.

## Measure before you trust a threshold

The command-line tool blocks nothing by default. It listens, and reports what your
mouse is actually doing:

```bash
# Watch live, blocking nothing
dotnet run --project src/ChatterFix.Cli

# Measure for ten minutes and write a report
dotnet run --project src/ChatterFix.Cli -- --quiet --seconds 600 --report report.json

# Log every click event as CSV: press length and release-to-press gap
dotnet run --project src/ChatterFix.Cli -- --quiet --seconds 300 --events events.csv

# Prove the hook is installed and can block events
dotnet run --project src/ChatterFix.Cli -- --selftest
```

A healthy mouse produces nothing below 30 ms. A faulty one shows a cluster down in
the single digits, separated from real clicks by an empty band — and that empty
band is where the threshold belongs.

A short measurement rarely settles it. Faults are rare, and the band only becomes
visible after thousands of clicks, so the tray application keeps its counters in
`%APPDATA%\ChatterFix\statistics.json` and carries them across restarts. Leave it
running for a few days and the statistics window will have an answer the first ten
minutes could not give.

## Configuration

Settings live in `%APPDATA%\ChatterFix\config.json`. The file can be edited by
hand; values outside a usable range are clamped on load, and a file that cannot be
parsed falls back to defaults rather than leaving the mouse unprotected.

## Project layout

```
src/
  ChatterFix.Core/          Filtering engine, no UI and no Win32 above the Native folder
    Filtering/              ClickFilter: the decision logic, testable without a mouse
    Native/                 Low-level hook, input injection, release scheduler
    Diagnostics/            Histograms, counters, event ring buffer
    Configuration/          Profiles, persistence, startup registration
  ChatterFix.App/           Tray application (WinForms)
  ChatterFix.Cli/           Diagnostics and measurement tool
tests/
  ChatterFix.Tests/         57 tests, including a 20,000-step balance invariant
```

## Pitfalls

Things that cost real debugging time here, and will cost it again in any project
that hooks Windows input:

1. **Mend a break in a hold; do not swallow the press after it.** A worn contact
   breaks for 15–160 ms mid-hold. If the release window is shorter than that, the
   release goes out and the drag ends no matter what happens to the next press.
   Breaks that long overlap a double click's pause, so size the window by how long
   the press has lasted, not by the gap alone. A press that is swallowed
   must take its release with it, or the button sticks down; a 20,000-step test
   asserts that balance.
2. **Measure from the release, not from the previous press.** Press-to-press looks
   correct until someone holds a button for two seconds; the fault that follows is
   seconds away from the last press and sails straight through.
3. **Keep the hook callback empty.** Windows silently removes a low-level hook whose
   callback takes longer than 300 ms, and the mouse then works with no sign of what
   happened. Anything expensive — resolving a window to a process name, for
   instance — belongs on another thread.
4. **Hold the hook delegate in a field.** Passing a lambda straight to
   `SetWindowsHookEx` lets the garbage collector reclaim it, and Windows then jumps
   into freed memory at the next click.
5. **Do not click fast on purpose while measuring.** Deliberate fast clicking lands
   in the same interval range as chatter, so the measurement shows a fault that is
   really your own finger. Measure during ordinary use.

## A note on what this can and cannot do

This is a software repair for a mechanical failure. The switch does not get better,
and as it wears the faulty intervals creep upwards towards real clicking speed. The
statistics window is there to show that happening: when the histogram stops having
an empty band between faults and real clicks, no threshold can separate them any
more, and the switch needs replacing.

## License

[MIT](LICENSE) © Seyid Arda Zeybek
