// iX/GX Extra Stages
// Azure Striker Gunvolt GX (game4.exe) has two modes in one exe: GX's main story and iX Connect (internally
// "Break Outlander"). This follows whichever mode is running:
//   GX story:   adds GV3's two stages, Third Revenger (stage 30, Nova) and Grand Master (stage 31, Asimov),
//               to GX's own mission select (Secret Mission 15 / Boss Rush page).
//   iX Connect: lists every GX mission plus GV3's two stages, lets you pick the GX difficulty
//               (STANDARD / DEMANDING / MERCILESS TASK) for the next Dispatch, and can swap iX's own Sunrise
//               Palace for GX's Sunrise Palace 3 + Sunrise Throne.
// Both modes get the Asimov crash fix. Works purely in memory; no game files are modified.
//
// Missions: GX looks stages up in its 30-entry list (RVA 0x1ccc0e0..0x1ccc680), iX Connect in its own 5-entry
// list right after it (0x1ccc680..0x1ccc770). A 32-entry list (GX's 30 + Third Revenger and Grand Master on the
// Boss Rush page, both with Boss Rush's GV + Copen banner) is built in memory allocated next to the game and every
// lookup in both modes uses it (9 GX + 9 iX start/end pairs). GX's menu builder only adds a mission whose stage
// id bit is set in the unlock bitmask (0x1dfb8c0); ids 30/31 never get one, so the check lets ids >= 30 through.
// iX's menu has a Sunrise Palace unlock gate instead, replaced by a "hide one id" check.
//
// Maps: each mode has its own stage/area descriptor table (map script hash per stage area). iX's
// (0x170cbd0, 39 rows) only has maps for its five stages; every other row is blank and crashes the
// loader. A 78-row table is built: iX rows with the blanks filled from GX's matching rows, then GX's
// rows 39+ at their GX row numbers (other row-indexed tables in the game use GX numbering). Every
// iX-branch reference to the table (31 lea sites, various fields) is redirected to it and the 16 iX row
// limits go from 0x27 to 0x4e. The per-row stage logic pointer table (iX 0x1716f20, GX 0x1716cb0) gets
// the same treatment. Rows 36-38 are either iX's three-area Sunrise Palace (stage 13; Sunrise Throne,
// stage 14, then has no first area and is hidden from the menu) or GX's own rows 36-38 (Sunrise Palace 3's
// two areas + Sunrise Throne's first area; nothing hidden), depending on the Sunrise switch.
//
// Difficulty: at Dispatch the game copies the selected difficulty ([sel+0x11c]: 0 normal, 1 hard,
// 2 very hard) into the stage object (+0x664) and the save (RVA 0xdb060b, 0xdb061e). iX Connect never
// changes it from 0; those two loads are replaced by constants while a harder task is picked.
//
// Crash fix: Grand Master's boss (cBsWork86) reads Gunvolt's player slot without a null check in one attack;
// playing as Copen it is always empty. A guard in the block skips that check when there is no player (see
// AsimovCheckRva). It is installed as soon as the game is up, whether or not the stages are switched on.
//
// Build: build.cmd, or csc /target:winexe /platform:x64 /optimize "/out:iX-GX Extra Stages.exe" src\IxGxExtraStages.cs
// Text: build with /define:JAPANESE for the Japanese version ("iX-GX エクストラステージ.exe"). Terms come from the game's own text
// (ミッション選択, 出撃, スペシャルミッション, 太陽宮, 太陽宮玉座, 双極の紫背者, 新世界の王; the Japanese difficulty
// names are T A S K / SEVERE TASK / RECKLESS TASK). iX Connect's only Japanese name in the game text is its
// result title "CONNECT X".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Native {
    [DllImport("kernel32")] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
    [DllImport("kernel32")] public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint prot, out uint old);
    [DllImport("kernel32")] public static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);
    [DllImport("kernel32")] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint prot);
}

class Patch {
    public long Rva; public byte[] Original; public byte[] Replacement;
    public Patch(long rva, byte[] orig, byte[] repl) { Rva = rva; Original = orig; Replacement = repl; }
}

// A "lea reg, [rip + X]" instruction to point at Offset inside the allocated block.
class CaveRef {
    public long Rva; public byte[] Original; public int Offset;
    public CaveRef(long rva, byte[] orig, int offset) { Rva = rva; Original = orig; Offset = offset; }
}

static class L {
#if JAPANESE
    public const string FontName = "Yu Gothic UI";
    public const string Title = "iX/GX エクストラステージ";
    public const string Waiting = "GX（game4.exe）の起動を待っています…\nトリロジーのランチャーからGXまたはCONNECT Xを起動してください。";
    public const string Starting = "GXを起動中…";
    public const string ModeGx = "GXのストーリーモードで動作中。";
    public const string ModeIx = "CONNECT Xで動作中。";
    public const string Off = "チェックを入れるとステージが追加されます。";
    public const string On = "有効：ミッション選択を開き直すと追加されたステージが表示されます。";
    public const string OpenFailed = "GXを開けませんでした（管理者として実行してみてください）。";
    public const string AttachFailed = "GXに接続できませんでした：";
    public const string EnableBoxGx = "GV3の双極の紫背者・新世界の王をミッション選択に追加";
    public const string EnableBoxIx = "GXの全ミッション＋GV3の双極の紫背者・新世界の王をCONNECT Xに追加";
    public const string EnableTip = "GV3で追加された紫電戦（双極の紫背者）とアシモフ戦（新世界の王）が、\nスペシャルミッション１５（ボスラッシュ）のページに並びます。\nCONNECT XではGXの全ミッションも選べます。\nステージ中にオフにすると結果画面が壊れることがあるので、メニューに戻るまでオンのままにしてください。";
    public const string ThroneBox = "GXの太陽宮３＋太陽宮玉座（CONNECT Xの太陽宮の代わり）";
    public const string ThroneTip = "オフ：CONNECT X版の太陽宮（太陽宮玉座は表示されません）。\nオン：GXの太陽宮３と太陽宮玉座（CONNECT X版の太陽宮は選べません）。\n変更後はミッション選択を開き直してください。";
    public const string DifficultyLabel = "難易度：";
    public static readonly string[] Difficulties = { "T A S K（通常）", "SEVERE TASK（ハード）", "RECKLESS TASK（ベリーハード）" };
    public const string DifficultyTip = "CONNECT Xの次の「出撃」に、どのステージでも適用されます。\nGXのハード／ベリーハードのルールを使います。";
    public const string BackupInfo = "初めて何かをオンにした時、セーブデータを自動でバックアップします。";
    public const string BackedUpTo = "セーブデータのバックアップ先：\n";
    public const string Restore = "最後のバックアップからセーブを復元";
    public const string RestoreTip = "このセッション前の状態にセーブデータを戻します。\n先にゲームを終了してください。";
    public const string NoSaveDir = "セーブフォルダが見つかりませんでした：\n";
    public const string BackupFailed = "セーブのバックアップに失敗したため、何も変更していません：\n";
    public const string CloseGameFirst = "先にGXを終了してから復元してください。";
    public const string ConfirmRestore1 = "現在のセーブデータを、次のバックアップで置き換えますか？\n";
    public const string ConfirmRestoreTitle = "セーブの復元";
    public const string Restored = "セーブデータを復元しました。";
    public const string BackupFolder = "セーブバックアップ";
    public const string TableFailed = "GXのメモリにステージ表を用意できなかったため、何も変更していません。";
#else
    public const string FontName = "Segoe UI";
    public const string Title = "iX/GX Extra Stages";
    public const string Waiting = "Waiting for GX (game4.exe)...\nStart GX or iX Connect from the Trilogy launcher.";
    public const string Starting = "GX is starting up...";
    public const string ModeGx = "Running in GX's main story.";
    public const string ModeIx = "Running in iX Connect.";
    public const string Off = "Check the box to add the stages.";
    public const string On = "Active: open (or re-open) the mission select to see the added stages.";
    public const string OpenFailed = "Could not open GX (try running this as administrator).";
    public const string AttachFailed = "Could not attach to GX: ";
    public const string EnableBoxGx = "Add GV3's Third Revenger and Grand Master stages to the mission select";
    public const string EnableBoxIx = "Show every GX mission + GV3's Third Revenger and Grand Master in iX Connect";
    public const string EnableTip = "Adds GV3's Nova fight (Third Revenger) and Asimov fight (Grand Master) to the\nSecret Mission 15 (Boss Rush) page. In iX Connect every GX mission is listed too.\nLeave it on until you're back on the menu; switching it off mid-stage can break the results screen.";
    public const string ThroneBox = "GX's Sunrise Palace 3 + Sunrise Throne (instead of iX's Sunrise Palace)";
    public const string ThroneTip = "Unticked: iX Connect's own Sunrise Palace (Sunrise Throne not listed).\nTicked: GX's Sunrise Palace 3 and Sunrise Throne (iX's Sunrise Palace not available).\nRe-open the mission select after changing this.";
    public const string DifficultyLabel = "Difficulty:";
    public static readonly string[] Difficulties = { "STANDARD TASK (game default)", "DEMANDING TASK (hard)", "MERCILESS TASK (very hard)" };
    public const string DifficultyTip = "Applies to the next Dispatch in iX Connect, on any stage.\nUses GX's own hard / very hard rules.";
    public const string BackupInfo = "Your save is backed up automatically the first time you switch something on.";
    public const string BackedUpTo = "Save backed up to:\n";
    public const string Restore = "Restore last save backup";
    public const string RestoreTip = "Puts your save back exactly as it was before this session.\nClose the game first.";
    public const string NoSaveDir = "Couldn't find your save folder:\n";
    public const string BackupFailed = "Save backup failed, so nothing was changed:\n";
    public const string CloseGameFirst = "Close GX first, then restore.";
    public const string ConfirmRestore1 = "Replace your current save with the backup from\n";
    public const string ConfirmRestoreTitle = "Restore save";
    public const string Restored = "Save restored.";
    public const string BackupFolder = "save backups";
    public const string TableFailed = "Couldn't set up the stage tables in GX's memory, so nothing was changed.";
#endif
}

class LauncherForm : Form {
    // Layout of the block allocated next to the game
    const int TableOffset = 0x0, LogicOffset = 0xa00, ListOffset = 0x1000, AllocSize = 0x2000;

    const long GxListRva = 0x1ccc0e0;
    const int ListEntrySize = 0x30, GxListEntries = 30, ListEntries = GxListEntries + 2;
    const int ListEnd = ListOffset + ListEntries * ListEntrySize;
    const byte ThirdRevengerStage = 30, GrandMasterStage = 31, SunriseThroneStage = 14, NoStage = 0xff;
    const uint ThirdRevengerNameHash = 0x37e604e2; // text key "StageName31" ("Third Revenger")
    const uint GrandMasterNameHash = 0x37e6056f;   // text key "StageName32" ("Grand Master")
    const byte BossRushChapter = 11, BossRushStage = 28;

    // iX mission lookups and menu builder: "lea reg, [iX list]" / "lea reg, [iX list end]"
    static readonly CaveRef[] IxListLeas = {
        new CaveRef(0xdb3751, new byte[] { 0x48, 0x8d, 0x15, 0x28, 0x8f, 0xf1, 0x00 }, ListOffset),
        new CaveRef(0xdb3758, new byte[] { 0x48, 0x8d, 0x05, 0x11, 0x90, 0xf1, 0x00 }, ListEnd),
        new CaveRef(0xe6cb30, new byte[] { 0x48, 0x8d, 0x35, 0x49, 0xfb, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe6cb37, new byte[] { 0x48, 0x8d, 0x0d, 0x32, 0xfc, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe6f594, new byte[] { 0x48, 0x8d, 0x1d, 0xe5, 0xd0, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe6f59b, new byte[] { 0x48, 0x8d, 0x0d, 0xce, 0xd1, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe6fa33, new byte[] { 0x48, 0x8d, 0x1d, 0x46, 0xcc, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe6fa3a, new byte[] { 0x48, 0x8d, 0x0d, 0x2f, 0xcd, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe708c2, new byte[] { 0x48, 0x8d, 0x3d, 0xb7, 0xbd, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe708c9, new byte[] { 0x48, 0x8d, 0x05, 0xa0, 0xbe, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe72d15, new byte[] { 0x48, 0x8d, 0x1d, 0x64, 0x99, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe72d1c, new byte[] { 0x4c, 0x8d, 0x3d, 0x4d, 0x9a, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe75ce0, new byte[] { 0x48, 0x8d, 0x35, 0x99, 0x69, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe75ce7, new byte[] { 0x48, 0x8d, 0x05, 0x82, 0x6a, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe780b6, new byte[] { 0x48, 0x8d, 0x0d, 0xc3, 0x45, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe780bd, new byte[] { 0x48, 0x8d, 0x15, 0xac, 0x46, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe745d9, new byte[] { 0x48, 0x8d, 0x1d, 0xa0, 0x80, 0xe5, 0x00 }, ListOffset),   // menu builder
        new CaveRef(0xe745e0, new byte[] { 0x48, 0x8d, 0x3d, 0x89, 0x81, 0xe5, 0x00 }, ListEnd),
    };

    // GX mission lookups and menu builder: "lea reg, [GX list]" / "lea reg, [GX list end]"
    static readonly CaveRef[] GxListLeas = {
        new CaveRef(0xdb3718, new byte[] { 0x48, 0x8d, 0x0d, 0xc1, 0x89, 0xf1, 0x00 }, ListOffset),
        new CaveRef(0xdb3734, new byte[] { 0x48, 0x8d, 0x05, 0x45, 0x8f, 0xf1, 0x00 }, ListEnd),
        new CaveRef(0xe6cafe, new byte[] { 0x48, 0x8d, 0x15, 0xdb, 0xf5, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe6cb17, new byte[] { 0x48, 0x8d, 0x0d, 0x62, 0xfb, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe6f561, new byte[] { 0x48, 0x8d, 0x15, 0x78, 0xcb, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe6f57d, new byte[] { 0x48, 0x8d, 0x0d, 0xfc, 0xd0, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe6fa08, new byte[] { 0x48, 0x8d, 0x15, 0xd1, 0xc6, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe6fa1c, new byte[] { 0x48, 0x8d, 0x0d, 0x5d, 0xcc, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe7088e, new byte[] { 0x48, 0x8d, 0x0d, 0x4b, 0xb8, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe708aa, new byte[] { 0x48, 0x8d, 0x05, 0xcf, 0xbd, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe72d23, new byte[] { 0x4c, 0x8d, 0x25, 0xb6, 0x93, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe72d2a, new byte[] { 0x4c, 0x8d, 0x35, 0x4f, 0x99, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe74518, new byte[] { 0x48, 0x8d, 0x1d, 0xc1, 0x7b, 0xe5, 0x00 }, ListOffset),   // menu builder
        new CaveRef(0xe74526, new byte[] { 0x48, 0x8d, 0x3d, 0x53, 0x81, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe75caa, new byte[] { 0x48, 0x8d, 0x0d, 0x2f, 0x64, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe75cc6, new byte[] { 0x48, 0x8d, 0x05, 0xb3, 0x69, 0xe5, 0x00 }, ListEnd),
        new CaveRef(0xe78083, new byte[] { 0x4c, 0x8d, 0x05, 0x56, 0x40, 0xe5, 0x00 }, ListOffset),
        new CaveRef(0xe7809f, new byte[] { 0x48, 0x8d, 0x15, 0xda, 0x45, 0xe5, 0x00 }, ListEnd),
    };

    // GX menu builder: add an entry only if bit <stage id> is set in the unlock bitmask [r14].
    // movzx ecx,[rbx+0xc]; mov edx,ecx; and edx,0x1f; mov eax,ecx; shr rax,5; mov eax,[r14+rax*4]; bt eax,edx; jae skip
    // -> movzx ecx,[rbx+0xc]; cmp ecx,30; jae add; mov eax,ecx; shr eax,5; mov eax,[r14+rax*4]; bt eax,ecx; jae skip; nop
    const long GxUnlockCheckRva = 0xe74530;
    static readonly byte[] GxUnlockCheckOriginal = {
        0x0f, 0xb6, 0x4b, 0x0c, 0x8b, 0xd1, 0x83, 0xe2, 0x1f, 0x8b, 0xc1, 0x48,
        0xc1, 0xe8, 0x05, 0x41, 0x8b, 0x04, 0x86, 0x0f, 0xa3, 0xd0, 0x73, 0x0d };
    static readonly byte[] GxUnlockCheckPatch = {
        0x0f, 0xb6, 0x4b, 0x0c, 0x83, 0xf9, ThirdRevengerStage, 0x73, 0x0f, 0x8b, 0xc1, 0xc1,
        0xe8, 0x05, 0x41, 0x8b, 0x04, 0x86, 0x0f, 0xa3, 0xc8, 0x73, 0x0e, 0x90 };

    // iX menu builder's id/unlock switch: "sub ecx, 1; je add" -> "cmp ecx, <hidden id>; jne add"
    // (the hidden id then falls into the original switch and is skipped)
    const long MenuGateRva = 0xe745eb;
    static readonly byte[] MenuGateOriginal = { 0x83, 0xe9, 0x01, 0x74, 0x54 };
    static byte[] MenuGate(byte hiddenId) { return new byte[] { 0x83, 0xf9, hiddenId, 0x75, 0x54 }; }

    // Every iX-branch "lea reg, [iX descriptor table + field]"; offset = field inside the 32-byte row
    static readonly CaveRef[] TableLeas = {
        new CaveRef(0xa3fd29, new byte[] { 0x48, 0x8d, 0x0d, 0xb9, 0xce, 0xcc, 0x00 }, 0x19),
        new CaveRef(0xa3fd35, new byte[] { 0x48, 0x8d, 0x05, 0xad, 0xce, 0xcc, 0x00 }, 0x19),
        new CaveRef(0xbe804a, new byte[] { 0x48, 0x8d, 0x0d, 0x97, 0x4b, 0xb2, 0x00 }, 0x18),
        new CaveRef(0xbe8056, new byte[] { 0x48, 0x8d, 0x05, 0x8b, 0x4b, 0xb2, 0x00 }, 0x18),
        new CaveRef(0xbfc665, new byte[] { 0x48, 0x8d, 0x0d, 0x80, 0x05, 0xb1, 0x00 }, 0x1c),
        new CaveRef(0xbfc675, new byte[] { 0x48, 0x8d, 0x05, 0x70, 0x05, 0xb1, 0x00 }, 0x1c),
        new CaveRef(0xd8c918, new byte[] { 0x48, 0x8d, 0x0d, 0xca, 0x02, 0x98, 0x00 }, 0x19),
        new CaveRef(0xd8c924, new byte[] { 0x48, 0x8d, 0x05, 0xbe, 0x02, 0x98, 0x00 }, 0x19),
        new CaveRef(0xd9d6fd, new byte[] { 0x4c, 0x8d, 0x0d, 0xe4, 0xf4, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9d8c3, new byte[] { 0x48, 0x8d, 0x15, 0x1e, 0xf3, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9d8cf, new byte[] { 0x48, 0x8d, 0x0d, 0x12, 0xf3, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9e282, new byte[] { 0x48, 0x8d, 0x0d, 0x5f, 0xe9, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9e28e, new byte[] { 0x48, 0x8d, 0x05, 0x53, 0xe9, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9f0f4, new byte[] { 0x48, 0x8d, 0x0d, 0xed, 0xda, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9f100, new byte[] { 0x48, 0x8d, 0x05, 0xe1, 0xda, 0x96, 0x00 }, 0x18),
        new CaveRef(0xd9f49d, new byte[] { 0x48, 0x8d, 0x05, 0x2c, 0xd7, 0x96, 0x00 }, 0x0),
        new CaveRef(0xd9f4a9, new byte[] { 0x48, 0x8d, 0x0d, 0x20, 0xd7, 0x96, 0x00 }, 0x0),
        new CaveRef(0xd9ffad, new byte[] { 0x48, 0x8d, 0x05, 0x24, 0xcc, 0x96, 0x00 }, 0x8),
        new CaveRef(0xd9ffb9, new byte[] { 0x48, 0x8d, 0x15, 0x18, 0xcc, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda05f0, new byte[] { 0x48, 0x8d, 0x05, 0xe1, 0xc5, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda05fc, new byte[] { 0x48, 0x8d, 0x15, 0xd5, 0xc5, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda0aeb, new byte[] { 0x48, 0x8d, 0x05, 0xe6, 0xc0, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda0af7, new byte[] { 0x48, 0x8d, 0x15, 0xda, 0xc0, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda1137, new byte[] { 0x48, 0x8d, 0x05, 0x9a, 0xba, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda1143, new byte[] { 0x48, 0x8d, 0x15, 0x8e, 0xba, 0x96, 0x00 }, 0x8),
        new CaveRef(0xda9b8a, new byte[] { 0x48, 0x8d, 0x05, 0x4f, 0x30, 0x96, 0x00 }, 0x10),
        new CaveRef(0xda9b9a, new byte[] { 0x48, 0x8d, 0x1d, 0x3f, 0x30, 0x96, 0x00 }, 0x10),
        new CaveRef(0xdd8e23, new byte[] { 0x4c, 0x8d, 0x1d, 0xa6, 0x3d, 0x93, 0x00 }, 0x0),
        new CaveRef(0xff1c7d, new byte[] { 0x48, 0x8d, 0x05, 0x5c, 0xaf, 0x71, 0x00 }, 0x10),
        new CaveRef(0xff1c89, new byte[] { 0x48, 0x8d, 0x1d, 0x50, 0xaf, 0x71, 0x00 }, 0x10),
    };

    // iX-branch descriptor row limits: 0x27 rows -> 0x4e (GX's row count)
    static readonly Patch[] RowLimitPatches = {
        new Patch(0xa3fd21, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xbe803d, new byte[] { 0x40, 0x80, 0xfe, 0x27 }, new byte[] { 0x40, 0x80, 0xfe, 0x4e }),
        new Patch(0xbfc65d, new byte[] { 0x80, 0xf9, 0x27 }, new byte[] { 0x80, 0xf9, 0x4e }),
        new Patch(0xd8c910, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xd9d8ba, new byte[] { 0x80, 0xf9, 0x27 }, new byte[] { 0x80, 0xf9, 0x4e }),
        new Patch(0xd9e27a, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xd9f0ec, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xd9f492, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xd9ffa2, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xda05e5, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xda0ae0, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xda112c, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xda9b83, new byte[] { 0x3c, 0x27 }, new byte[] { 0x3c, 0x4e }),
        new Patch(0xff1c71, new byte[] { 0x80, 0xfa, 0x27 }, new byte[] { 0x80, 0xfa, 0x4e }),
        new Patch(0xd9d77b, new byte[] { 0x80, 0xf9, 0x27 }, new byte[] { 0x80, 0xf9, 0x4e }),
        new Patch(0xdd8e52, new byte[] { 0x80, 0xf9, 0x27 }, new byte[] { 0x80, 0xf9, 0x4e }),
    };

    // "mov rcx, [rcx + rbp*8 + iX logic table]" (rcx = image base): disp32 -> combined pointer table
    const long LogicReadRva = 0xb001cf;
    static readonly byte[] LogicReadOriginal = { 0x48, 0x8b, 0x8c, 0xe9, 0x20, 0x6f, 0x71, 0x01 };

    // Grand Master (cBsWork86, Asimov) co_Act_Attack02 checks "player state == 4" through the stage object's
    // player slot (+0x628) without a null check; with no player there (Copen down) the game closes to the desktop.
    // The check jumps to a guard in the block that treats a missing player as "not in state 4".
    const long AsimovCheckRva = 0x8ed975, AsimovStateNotFourRva = 0x8edac2, AsimovContinueRva = 0x8ed98c;
    const int AsimovGuardOffset = 0x1800;
    // mov rcx,[rax+0x628]; xorps xmm6,xmm6; cmp dword [rcx+0xc4],4; jne state-not-4
    static readonly byte[] AsimovCheckOriginal = {
        0x48, 0x8b, 0x88, 0x28, 0x06, 0x00, 0x00, 0x0f, 0x57, 0xf6,
        0x83, 0xb9, 0xc4, 0x00, 0x00, 0x00, 0x04, 0x0f, 0x85, 0x36, 0x01, 0x00, 0x00 };

    const long IxTableRva = 0x170cbd0, GxTableRva = 0x170d0b0;
    const long IxLogicRva = 0x1716f20, GxLogicRva = 0x1716cb0, NullLogicRva = 0x1c99250;
    const int RowSize = 32, IxRows = 39, GxRows = 78, SunriseRow = 36, SunriseRows = 3;

    // Dispatch: "mov ecx, [rax+0x11c]" / "mov r8d, [rax+0x11c]" (selected difficulty) -> constants
    const long DifficultyRva1 = 0xdb060b, DifficultyRva2 = 0xdb061e;
    static readonly byte[] DifficultyOriginal1 = { 0x8b, 0x88, 0x1c, 0x01, 0x00, 0x00 };
    static readonly byte[] DifficultyOriginal2 = { 0x44, 0x8b, 0x80, 0x1c, 0x01, 0x00, 0x00 };
    const long ModeRva = 0x1e1ba08;   // 1 = GX main story, 0 = iX Connect

    static readonly string SaveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "INTI CREATES");
    static readonly string BackupRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, L.BackupFolder);

    readonly Label status = new Label();
    readonly CheckBox enableBox = new CheckBox();
    readonly CheckBox throneBox = new CheckBox();
    readonly Label difficultyLabel = new Label();
    readonly ComboBox difficultyBox = new ComboBox();
    readonly Button restoreButton = new Button();
    readonly Label backupLabel = new Label();
    readonly Timer timer = new Timer();
    readonly ToolTip tips = new ToolTip();

    Process game;
    IntPtr handle = IntPtr.Zero;
    long moduleBase;
    long cave;               // allocated block; 0 until built (lives as long as the game process)
    Patch[] stagePatches;    // everything behind the "every stage" box; built with the block
    byte[] ixSunriseRows, gxSunriseRows, ixSunriseLogic, gxSunriseLogic;
    byte[] guardJump;        // "jmp guard" for AsimovCheckRva; built with the block
    bool blockFailed, guardOwned;
    bool patched;
    int appliedThrone = -1;  // Sunrise switch state written into the block (-1 = not yet)
    int appliedDifficulty;   // 0 = game's own value, else the forced value
    bool isIx;
    bool backedUpThisSession;
    string lastBackup;

    LauncherForm(bool startEnabled) {
        Text = L.Title;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(480, 250);
        Font = new Font(L.FontName, 9f);

        status.SetBounds(12, 12, 456, 40);
        status.Text = L.Waiting;

        enableBox.SetBounds(12, 58, 456, 24);
        enableBox.Text = L.EnableBoxGx;
        enableBox.Checked = startEnabled;
        enableBox.CheckedChanged += (s, e) => ApplyState();
        tips.SetToolTip(enableBox, L.EnableTip);

        throneBox.SetBounds(32, 84, 436, 24);
        throneBox.Text = L.ThroneBox;
        throneBox.CheckedChanged += (s, e) => ApplyState();
        tips.SetToolTip(throneBox, L.ThroneTip);

        difficultyLabel.SetBounds(12, 117, 70, 20);
        difficultyLabel.Text = L.DifficultyLabel;
        difficultyBox.SetBounds(84, 114, 260, 24);
        difficultyBox.DropDownStyle = ComboBoxStyle.DropDownList;
        difficultyBox.Items.AddRange(L.Difficulties);
        difficultyBox.SelectedIndex = 0;
        difficultyBox.SelectedIndexChanged += (s, e) => ApplyState();
        tips.SetToolTip(difficultyBox, L.DifficultyTip);

        backupLabel.SetBounds(12, 150, 456, 40);
        backupLabel.Text = L.BackupInfo;
        backupLabel.ForeColor = SystemColors.GrayText;

        restoreButton.SetBounds(12, 204, 260, 30);
        restoreButton.Text = L.Restore;
        restoreButton.Enabled = false;
        restoreButton.Click += (s, e) => RestoreBackup();
        tips.SetToolTip(restoreButton, L.RestoreTip);

        Controls.AddRange(new Control[] { status, enableBox, throneBox, difficultyLabel, difficultyBox, backupLabel, restoreButton });

        timer.Interval = 500;
        timer.Tick += (s, e) => Poll();
        timer.Start();
        FormClosing += (s, e) => { RevertAll(); Detach(); };
        lastBackup = FindLatestBackup();
        restoreButton.Enabled = lastBackup != null;
    }

    void Poll() {
        if (game == null || game.HasExited) {
            Detach();
            var found = LiveGame();
            if (found == null) { SetStatus(L.Waiting); return; }
            Attach(found);
            if (game == null) return;
        }
        if (!CodeIsReady()) { SetStatus(L.Starting); return; }
        if (cave == 0 && !blockFailed && !BuildBlock()) blockFailed = true;
        if (blockFailed) { enableBox.Checked = false; SetStatus(L.TableFailed); return; }
        InstallGuard();
        ApplyState();
        ShowMode();
        SetStatus((isIx ? L.ModeIx : L.ModeGx) + "\n" + (patched ? L.On : L.Off));
    }

    // iX Connect has the difficulty picker and the Sunrise switch; GX's story has its own difficulty select.
    void ShowMode() {
        enableBox.Text = isIx ? L.EnableBoxIx : L.EnableBoxGx;
        throneBox.Visible = difficultyLabel.Visible = difficultyBox.Visible = isIx;
    }

    // Leaves the site alone unless it holds the original code (another tool may already have patched it).
    void InstallGuard() {
        if (guardOwned || guardJump == null || !Holds(AsimovCheckRva, AsimovCheckOriginal, null)) return;
        Write(AsimovCheckRva, guardJump);
        guardOwned = true;
    }

    // A closed game can stay listed while another program (Steam, Game Bar) still holds a handle to it; skip those.
    static Process LiveGame() {
        foreach (var p in Process.GetProcessesByName("game4")) {
            try { if (!p.HasExited) return p; } catch (Exception) { }
        }
        return null;
    }

    void Attach(Process p) {
        try {
            handle = Native.OpenProcess(0x0438, false, p.Id); // VM_OPERATION | VM_READ | VM_WRITE | QUERY_INFORMATION
            if (handle == IntPtr.Zero) { SetStatus(L.OpenFailed); return; }
            moduleBase = p.MainModule.BaseAddress.ToInt64();
            game = p;
            cave = 0; stagePatches = null; guardJump = null; blockFailed = false; guardOwned = false;
            patched = false; appliedThrone = -1; appliedDifficulty = 0;
        } catch (Exception ex) {
            SetStatus(L.AttachFailed + ex.Message);
            Detach();
        }
    }

    void Detach() {
        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
        handle = IntPtr.Zero; game = null; patched = false;
    }

    // The exe is SteamStub-encrypted on disk; its code only matches once the game has unpacked itself.
    // Every site must hold either its original bytes or what this launcher wrote there.
    bool CodeIsReady() {
        if (stagePatches != null) {
            foreach (var p in stagePatches) if (!Holds(p.Rva, p.Original, p.Replacement)) return false;
        } else {
            foreach (var r in IxListLeas) if (!Holds(r.Rva, r.Original, null)) return false;
            foreach (var r in GxListLeas) if (!Holds(r.Rva, r.Original, null)) return false;
            foreach (var r in TableLeas) if (!Holds(r.Rva, r.Original, null)) return false;
            foreach (var p in RowLimitPatches) if (!Holds(p.Rva, p.Original, null)) return false;
            if (!Holds(MenuGateRva, MenuGateOriginal, null) || !Holds(LogicReadRva, LogicReadOriginal, null) ||
                !Holds(GxUnlockCheckRva, GxUnlockCheckOriginal, null)) return false;
        }
        if (!Holds(DifficultyRva1, DifficultyOriginal1, DifficultyPatch1(appliedDifficulty)) ||
            !Holds(DifficultyRva2, DifficultyOriginal2, DifficultyPatch2(appliedDifficulty))) return false;
        var mode = Read(ModeRva, 4);
        if (mode == null) return false;
        isIx = BitConverter.ToInt32(mode, 0) == 0;
        return true;
    }

    bool Holds(long rva, byte[] a, byte[] b) {
        var cur = Read(rva, a.Length);
        return cur != null && (Same(cur, a) || (b != null && Same(cur, b)));
    }

    void ApplyState() {
        if (game == null || !CodeIsReady()) return;
        int wantDifficulty = isIx ? difficultyBox.SelectedIndex : 0;
        if ((enableBox.Checked || wantDifficulty != 0) && !BackupSave()) {
            enableBox.Checked = false; difficultyBox.SelectedIndex = 0; return;
        }

        if (enableBox.Checked && !patched) {
            if (stagePatches == null && !BuildBlock()) { enableBox.Checked = false; SetStatus(L.TableFailed); return; }
            foreach (var p in stagePatches) Write(p.Rva, p.Replacement);
            patched = true;
        } else if (!enableBox.Checked && patched) {
            foreach (var p in stagePatches) Write(p.Rva, p.Original);
            patched = false;
        }
        if (cave != 0 && appliedThrone != (throneBox.Checked ? 1 : 0)) ApplyThrone(throneBox.Checked);

        if (wantDifficulty != appliedDifficulty) {
            Write(DifficultyRva1, wantDifficulty == 0 ? DifficultyOriginal1 : DifficultyPatch1(wantDifficulty));
            Write(DifficultyRva2, wantDifficulty == 0 ? DifficultyOriginal2 : DifficultyPatch2(wantDifficulty));
            appliedDifficulty = wantDifficulty;
        }
    }

    // Puts the game's code back (the allocated block stays), so a later launcher finds original bytes.
    void RevertAll() {
        if (game == null || game.HasExited) return;
        if (patched) foreach (var p in stagePatches) Write(p.Rva, p.Original);
        if (appliedDifficulty != 0) { Write(DifficultyRva1, DifficultyOriginal1); Write(DifficultyRva2, DifficultyOriginal2); }
        if (guardOwned) Write(AsimovCheckRva, AsimovCheckOriginal);
        patched = false; appliedDifficulty = 0; guardOwned = false;
    }

    static byte[] DifficultyPatch1(int d) { return d == 0 ? null : new byte[] { 0xb9, (byte)d, 0, 0, 0, 0x90 }; }          // mov ecx, d
    static byte[] DifficultyPatch2(int d) { return d == 0 ? null : new byte[] { 0x41, 0xb8, (byte)d, 0, 0, 0, 0x90 }; }    // mov r8d, d

    // Rows 36-38: iX's Sunrise Palace (Sunrise Throne hidden) or GX's Sunrise Palace 3 + Sunrise Throne.
    void ApplyThrone(bool gxRows) {
        WriteAbs(cave + TableOffset + SunriseRow * RowSize, gxRows ? gxSunriseRows : ixSunriseRows);
        WriteAbs(cave + LogicOffset + SunriseRow * 8, gxRows ? gxSunriseLogic : ixSunriseLogic);
        var gate = MenuGate(gxRows ? NoStage : SunriseThroneStage);
        bool throne = gxRows;
        foreach (var p in stagePatches) if (p.Rva == MenuGateRva) p.Replacement = gate;
        if (patched) Write(MenuGateRva, gate);
        appliedThrone = throne ? 1 : 0;
    }

    // Allocates the block within rel32 reach of the game's code and fills the descriptor table, the
    // logic pointer table and the mission list.
    bool BuildBlock() {
        var ix = Read(IxTableRva, IxRows * RowSize);
        var gx = Read(GxTableRva, GxRows * RowSize);
        var ixLogic = Read(IxLogicRva, IxRows * 8);
        var gxLogic = Read(GxLogicRva, GxRows * 8);
        var gxList = Read(GxListRva, GxListEntries * ListEntrySize);
        if (ix == null || gx == null || ixLogic == null || gxLogic == null || gxList == null) return false;

        // stage logic per row: iX's where it has a real one, otherwise GX's (same row layout as below)
        var logic = new byte[GxRows * 8];
        long nullLogic = moduleBase + NullLogicRva;
        for (int r = 0; r < 36; r++)
            Array.Copy(BitConverter.ToInt64(ixLogic, r * 8) == nullLogic ? gxLogic : ixLogic, r * 8, logic, r * 8, 8);
        Array.Copy(ixLogic, 36 * 8, logic, 36 * 8, 3 * 8);
        Array.Copy(gxLogic, IxRows * 8, logic, IxRows * 8, (GxRows - IxRows) * 8);

        var table = new byte[GxRows * RowSize];
        Array.Copy(ix, table, ix.Length);
        // rows 0-35 cover the same stage/area pairs (+0x18) in both tables; fill iX's blank map hashes
        for (int r = 0; r < 36; r++)
            if (BitConverter.ToUInt32(table, r * RowSize) == 0 &&
                BitConverter.ToUInt32(table, r * RowSize + 0x18) == BitConverter.ToUInt32(gx, r * RowSize + 0x18))
                Array.Copy(gx, r * RowSize, table, r * RowSize, 8);
        // iX rows 36-38 are its own Sunrise Palace (stage 13); GX rows 39+ keep their GX row numbers
        Array.Copy(gx, IxRows * RowSize, table, IxRows * RowSize, (GxRows - IxRows) * RowSize);
        ixSunriseRows = Slice(table, SunriseRow * RowSize, SunriseRows * RowSize);
        gxSunriseRows = Slice(gx, SunriseRow * RowSize, SunriseRows * RowSize);
        ixSunriseLogic = Slice(logic, SunriseRow * 8, SunriseRows * 8);
        gxSunriseLogic = Slice(gxLogic, SunriseRow * 8, SunriseRows * 8);

        // GX's 30 missions + the two extra stages, cloned from the stage 26 entry, on the Boss Rush page
        var list = new byte[ListEntries * ListEntrySize];
        Array.Copy(gxList, list, gxList.Length);
        int template = 0, bossRush = 0;
        for (int i = 0; i < GxListEntries; i++) {
            if (gxList[i * ListEntrySize + 0xc] == 26) template = i * ListEntrySize;
            if (gxList[i * ListEntrySize + 0xc] == BossRushStage) bossRush = i * ListEntrySize;
        }
        AddListEntry(list, gxList, template, bossRush, GxListEntries, ThirdRevengerStage, ThirdRevengerNameHash);
        AddListEntry(list, gxList, template, bossRush, GxListEntries + 1, GrandMasterStage, GrandMasterNameHash);

        for (long a = (moduleBase - 0x10000) & ~0xffffL; a > moduleBase - 0x70000000L; a -= 0x10000) {
            var got = Native.VirtualAllocEx(handle, new IntPtr(a), new IntPtr(AllocSize), 0x3000, 0x40); // COMMIT|RESERVE, EXECUTE_READWRITE (holds the Asimov guard)
            if (got != IntPtr.Zero) { cave = got.ToInt64(); break; }
        }
        if (cave == 0) return false;
        if (!WriteAbs(cave + TableOffset, table) || !WriteAbs(cave + LogicOffset, logic) || !WriteAbs(cave + ListOffset, list) ||
            !WriteAbs(cave + AsimovGuardOffset, AsimovGuard())) return false;

        var patches = new List<Patch>(RowLimitPatches);
        foreach (var r in IxListLeas) patches.Add(LeaPatch(r));
        foreach (var r in GxListLeas) patches.Add(LeaPatch(r));
        foreach (var r in TableLeas) patches.Add(LeaPatch(r));
        var logicRepl = (byte[])LogicReadOriginal.Clone();
        BitConverter.GetBytes((int)(cave + LogicOffset - moduleBase)).CopyTo(logicRepl, 4);
        patches.Add(new Patch(LogicReadRva, LogicReadOriginal, logicRepl));
        patches.Add(new Patch(MenuGateRva, MenuGateOriginal, MenuGate(SunriseThroneStage)));
        patches.Add(new Patch(GxUnlockCheckRva, GxUnlockCheckOriginal, GxUnlockCheckPatch));
        stagePatches = patches.ToArray();
        guardJump = new byte[AsimovCheckOriginal.Length];   // jmp guard, rest nop
        for (int i = 0; i < guardJump.Length; i++) guardJump[i] = 0x90;
        guardJump[0] = 0xe9;
        BitConverter.GetBytes((int)(cave + AsimovGuardOffset - (moduleBase + AsimovCheckRva + 5))).CopyTo(guardJump, 1);
        appliedThrone = 0;
        return true;
    }

    // Neither stage has a banner of its own (the "prefix + stage number" sprites for 30/31 are blank),
    // so both use Boss Rush's GV + Copen banner (+0x1c sprite sheet, +0x20 sprite).
    static void AddListEntry(byte[] list, byte[] gxList, int template, int bossRush, int index, byte stage, uint nameHash) {
        int at = index * ListEntrySize;
        Array.Copy(gxList, template, list, at, ListEntrySize);
        BitConverter.GetBytes((uint)stage).CopyTo(list, at + 0x8);
        BitConverter.GetBytes((uint)(BossRushChapter << 8 | stage)).CopyTo(list, at + 0xc);
        BitConverter.GetBytes(nameHash).CopyTo(list, at + 0x18);
        Array.Copy(gxList, bossRush + 0x1c, list, at + 0x1c, 8);
    }

    // mov rcx,[rax+0x628]; xorps xmm6,xmm6; test rcx,rcx; jz notFour;
    // cmp dword [rcx+0xc4],4; jne notFour; jmp continue; notFour: jmp state-not-4
    byte[] AsimovGuard() {
        var g = new List<byte> { 0x48, 0x8b, 0x88, 0x28, 0x06, 0x00, 0x00, 0x0f, 0x57, 0xf6, 0x48, 0x85, 0xc9, 0x74, 0x0e,
                                 0x83, 0xb9, 0xc4, 0x00, 0x00, 0x00, 0x04, 0x75, 0x05 };
        long here = cave + AsimovGuardOffset;
        g.Add(0xe9); g.AddRange(BitConverter.GetBytes((int)(moduleBase + AsimovContinueRva - (here + g.Count + 4))));
        g.Add(0xe9); g.AddRange(BitConverter.GetBytes((int)(moduleBase + AsimovStateNotFourRva - (here + g.Count + 4))));
        return g.ToArray();
    }

    Patch LeaPatch(CaveRef r) {
        var repl = (byte[])r.Original.Clone();
        BitConverter.GetBytes((int)(cave + r.Offset - (moduleBase + r.Rva + 7))).CopyTo(repl, 3);
        return new Patch(r.Rva, r.Original, repl);
    }

    static byte[] Slice(byte[] a, int start, int n) { var r = new byte[n]; Array.Copy(a, start, r, 0, n); return r; }

    bool BackupSave() {
        if (backedUpThisSession) return true;
        try {
            if (!Directory.Exists(SaveDir)) { MessageBox.Show(L.NoSaveDir + SaveDir); return false; }
            string dest = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
            CopyDir(SaveDir, dest);
            lastBackup = dest;
            backedUpThisSession = true;
            restoreButton.Enabled = true;
            backupLabel.Text = L.BackedUpTo + dest;
            return true;
        } catch (Exception ex) {
            MessageBox.Show(L.BackupFailed + ex.Message);
            return false;
        }
    }

    void RestoreBackup() {
        if (lastBackup == null) return;
        if (LiveGame() != null) { MessageBox.Show(L.CloseGameFirst); return; }
        if (MessageBox.Show(L.ConfirmRestore1 + lastBackup, L.ConfirmRestoreTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        CopyDir(lastBackup, SaveDir);
        MessageBox.Show(L.Restored);
    }

    static string FindLatestBackup() {
        if (!Directory.Exists(BackupRoot)) return null;
        var dirs = Directory.GetDirectories(BackupRoot);
        if (dirs.Length == 0) return null;
        Array.Sort(dirs);
        return dirs[dirs.Length - 1];
    }

    static void CopyDir(string src, string dst) {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) {
            string rel = f.Substring(src.Length).TrimStart('\\');
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(f, target, true);
        }
    }

    byte[] Read(long rva, int n) {
        if (handle == IntPtr.Zero) return null;
        var buf = new byte[n]; IntPtr got;
        return Native.ReadProcessMemory(handle, new IntPtr(moduleBase + rva), buf, n, out got) && got.ToInt64() == n ? buf : null;
    }

    void Write(long rva, byte[] data) {
        var addr = new IntPtr(moduleBase + rva); uint old, tmp; IntPtr wrote;
        Native.VirtualProtectEx(handle, addr, new IntPtr(data.Length), 0x40, out old); // PAGE_EXECUTE_READWRITE
        Native.WriteProcessMemory(handle, addr, data, data.Length, out wrote);
        Native.VirtualProtectEx(handle, addr, new IntPtr(data.Length), old, out tmp);
        Native.FlushInstructionCache(handle, addr, new IntPtr(data.Length));
    }

    bool WriteAbs(long addr, byte[] data) {
        IntPtr wrote;
        return Native.WriteProcessMemory(handle, new IntPtr(addr), data, data.Length, out wrote);
    }

    static bool Same(byte[] a, byte[] b) {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    void SetStatus(string text) { status.Text = text; }

    // --on: start with every stage enabled; --throne: GX's Sunrise Palace 3 + Sunrise Throne; --difficulty=N (0-2)
    [STAThread]
    static void Main(string[] args) {
        Application.EnableVisualStyles();
        var form = new LauncherForm(Array.IndexOf(args, "--on") >= 0);
        if (Array.IndexOf(args, "--throne") >= 0) form.throneBox.Checked = true;
        foreach (var a in args)
            if (a.StartsWith("--difficulty=")) form.difficultyBox.SelectedIndex = Math.Max(0, Math.Min(2, int.Parse(a.Substring(13))));
        Application.Run(form);
    }
}
