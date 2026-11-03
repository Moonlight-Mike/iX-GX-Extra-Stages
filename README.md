# iX/GX Extra Stages

[日本語](README.ja.md)

A small Windows tool for **Azure Striker Gunvolt GX** in *Azure Striker Gunvolt Trilogy Enhanced* (Steam).
It adds stages to the mission select of whichever GX mode you're playing:

- **GX (main story):** adds Gunvolt 3's Third Revenger (boss: Nova) and Grand Master (boss: Asimov)
  stages to the mission select.
- **iX Connect:** lets you play every GX mission, plus Gunvolt 3's Third Revenger and Grand Master, as
  Copen from Luminous Avenger iX, and pick GX's difficulty for each Dispatch.

It also fixes a crash where the game closed to the desktop in the middle of the Grand Master fight.

Nothing on disk is changed. The tool only patches the running game's memory, and closing it puts the
game back to normal. Your save folder is backed up automatically before the first change.

## Download and use

Get the latest zip from the [Releases](../../releases) page (English or Japanese), unzip it anywhere and
run the exe. Full instructions: [How to use iX-GX Extra Stages.txt](docs/How%20to%20use%20iX-GX%20Extra%20Stages.txt).

Requirements: Windows 10 or 11 (64-bit) and the Steam version of Azure Striker Gunvolt Trilogy Enhanced.

## Building

The tool is a single C# file built with the C# compiler that ships with Windows (.NET Framework 4).
Run `build.cmd` to build both the English and the Japanese exe.

```bat
build.cmd
```

## How it works

[Modding notes - iX-GX Extra Stages.txt](docs/Modding%20notes%20-%20iX-GX%20Extra%20Stages.txt) documents
everything learned while making this tool: the game's file and data formats, stage ids, mission lists,
menu unlock rules, stage tables, the Grand Master crash and its fix, and every patch site. It's meant to
help anyone else modding GX, iX Connect or Gunvolt 3. Japanese version:
[改造メモ (iX-GX エクストラステージ).txt](docs/改造メモ%20(iX-GX%20エクストラステージ).txt).

## License

[MIT](LICENSE). This covers the tool's code and documentation only; the game and its assets belong to
their owners.

## Disclaimer

This is an unofficial fan-made tool, not affiliated with or endorsed by INTI CREATES. Azure Striker
Gunvolt and all related names belong to their owners. Use it at your own risk; keep the save backup the
tool makes.
