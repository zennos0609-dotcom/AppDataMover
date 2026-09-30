# AppData Mover · AppData 迁移助手

[English](#english) | 中文

把 `AppData`（Roaming / Local / LocalLow）里各软件的数据目录**安全地搬到其他盘**，并在原位置留下**目录联接（Junction）**——软件无感知，C 盘瞬间瘦身。

---

## 为什么做这个

C 盘爆满时，真正的大头往往不是程序本体，而是藏在 `C:\Users\<你>\AppData` 里的**软件数据**：Docker 的虚拟磁盘、uv/pip/npm 的缓存、编辑器与聊天软件的数据……动辄几十 GB。网上大多数"清理 C 盘"教程只会教你删临时文件，治标不治本。

Windows 的**目录联接**可以把文件夹搬走后，在原路径留一个"透明路标"，所有程序访问原路径都会自动落到新位置——不需要重装软件、不需要改注册表、软件完全无感知。本工具把这套机制做成了安全、可回滚的图形界面操作。

## 功能特性

- **一键扫描**：枚举 AppData 三个子目录，多线程测量每个文件夹体积，按大小排序
- **软件归因**：读取系统卸载注册表，自动匹配文件夹对应的软件（如 `kingsoft` → WPS Office）
- **智能分类**：
  - 🟢 **缓存**（uv、pip、npm-cache、CrashDumps……）——可放心迁移
  - 🔵 **应用数据**——关闭对应软件后可迁移
  - 🟠 **特殊处理**（Docker、WSL 等含虚拟磁盘的目录）——提示走官方导出/导入流程
  - 🔴 **系统保护**（Packages、Microsoft……）——禁止迁移，防止搞坏系统
- **占用检测**：基于 Windows Restart Manager API，精确列出哪些进程锁着目标文件夹，并可一键温和关闭
- **安全搬迁**：复制 → 校验文件数与字节数 → 原目录暂存 → 建立联接 → 通过联接验证 → 删除暂存。任何一步失败自动回滚，绝不留下"搬了一半"的状态
- **默认目标策略**：
  1. 软件主程序在其他盘 → 搬到 `<主程序目录>\AppData\` 下
  2. 主程序在 C 盘或未识别 → 搬到剩余空间最大的非系统盘 `X:\AppDataMoved\`
  3. 也可以自己选任意目录
- **随时搬回**：识别已有联接，一键把数据搬回 C 盘原位
- **中英双语界面**：右上角一键切换并记住选择；英文 Windows 自动以英文启动

## 使用方法

1. 运行 `AppDataMover.exe`（Windows 10/11 自带 .NET Framework，无需安装任何运行时）
2. 点「扫描 AppData」，等待体积测量完成
3. 选中一行 → 确认目标路径（或点「自定义目标」）→ 点「检测占用」确认无进程占用
4. 点「迁移选中项」，日志区会显示每一步的进展
5. 迁移后请启动对应软件验证一下

> ⚠️ 建议：搬移前退出对应软件；一次搬一个，验证正常再搬下一个。Docker、WSL 这类含虚拟磁盘的目录请用各自的官方迁移方式（本工具会提示而不直接动手）。

## 原理说明

```
迁移前：  C:\Users\you\AppData\Roaming\Foo        （真实数据，占用 C 盘）

迁移后：  C:\Users\you\AppData\Roaming\Foo  ──联接──>  D:\AppDataMoved\Roaming\Foo
          ↑ 程序访问原路径时，文件系统自动重定向到 D 盘，完全透明
```

与"快捷方式"的区别：快捷方式只是给资源管理器看的 `.lnk` 文件，程序不认；**目录联接是 NTFS 文件系统级的重解析点（Reparse Point）**，对所有程序透明，不需要管理员权限即可创建。

## 从源码构建

无需安装 .NET SDK，系统自带的编译器即可：

- 需要 Visual Studio 2022 自带的 Roslyn 编译器（`MSBuild\Current\Bin\Roslyn\csc.exe`），或任意新版 `csc`
- 双击 `build.bat` 即编译出单文件 `AppDataMover.exe`

## 自检模式

```
AppDataMover.exe --selftest report.txt
```

不开界面，直接扫描本机 AppData 并输出归因/分类/联接报告，用于排查问题。

## 免责声明

本工具直接操作用户数据目录，虽然每一步都有校验与回滚，但请仍在**重要数据有备份**的前提下使用。作者不对任何数据损失负责。

## License

MIT

---

<a name="english"></a>

## AppData Mover

Safely relocate per-app data folders out of `AppData` (Roaming / Local / LocalLow) to another drive, leaving a **directory junction** at the original path — apps keep working, your C: drive breathes again.

### Why

When C: fills up, the real culprit is usually not program binaries but **application data** buried in `C:\Users\<you>\AppData`: Docker's virtual disk, uv/pip/npm caches, editor and chat data — easily tens of GB. Most "clean your C drive" guides only tell you to delete temp files, which treats the symptom, not the cause.

An NTFS **junction** lets you move a folder away while leaving a transparent signpost at the original path: every program accessing the old path is silently redirected to the new location. No reinstall, no registry edits, apps notice nothing. This tool wraps that mechanism in a safe, rollback-capable GUI.

### Features

- **One-click scan**: enumerate the three AppData scopes, measure folder sizes in parallel, sort by size
- **App attribution**: reads the uninstall registry to map folders to installed software (e.g. `kingsoft` → WPS Office)
- **Smart classification**: 🟢 cache (safe) · 🔵 app data (close the app first) · 🟠 special (Docker/WSL vhdx — prompts official export/import) · 🔴 system-protected (blocked)
- **Lock detection**: uses the Windows Restart Manager API to list exactly which processes hold the folder, with one-click graceful shutdown
- **Safe move pipeline**: copy → verify file count & bytes → stash original → create junction → probe through it → delete stash. **Any failure rolls back automatically**
- **Default target policy**: next to the app's install dir when it's off C:; otherwise the roomiest non-system drive (`X:\AppDataMoved\`); or pick any folder yourself
- **Undo**: detects existing junctions and moves data back to C:
- **Bilingual UI**: 中文/English toggle with persistence; English Windows starts in English automatically
- **Built-in app catalog**: recognizes 40+ Chinese and international apps (WeChat, WPS, Discord, Steam, Spotify, NVIDIA…) even when registry matching fails

### Usage

1. Run `AppDataMover.exe` (any Windows 10/11 — .NET Framework is built in)
2. Click **Scan AppData** and wait for sizes
3. Select a row, confirm or customize the target, click **Check locks**
4. Click **Move selected**; watch the log
5. Launch the affected app once to verify

> ⚠️ Close the corresponding app first; move one folder at a time and verify. For Docker/WSL folders containing virtual disks, use their official export/import flows — the tool warns instead of touching them.

### How it works

```
Before:  C:\Users\you\AppData\Roaming\Foo            (real data, eating C:)

After:   C:\Users\you\AppData\Roaming\Foo ──junction──> D:\AppDataMoved\Roaming\Foo
         ↑ file system transparently redirects every access to D:
```

Unlike a shortcut (a `.lnk` only Explorer understands), a junction is a filesystem-level **reparse point**, transparent to all programs, and requires no admin rights to create.

### Build from source

No .NET SDK required — just the Roslyn compiler bundled with Visual Studio 2022 (`MSBuild\Current\Bin\Roslyn\csc.exe`), or any recent `csc`. Double-click `build.bat` to produce the single-file `AppDataMover.exe`.

### Self-test mode

```
AppDataMover.exe --selftest report.txt
```

Headless scan of the local AppData — attribution, classification, and junction report — for troubleshooting.

### Disclaimer

This tool operates on real user data. Every step is verified and rollback-capable, but please **back up important data** first. Use at your own risk.

### License

MIT

---

> 🤖 **AI 说明 / AI disclosure**：本项目代码由作者与 AI 助手（Kimi）结对开发，作者负责产品设计、真实环境测试与全部技术决策。
> This project's code was pair-developed by the author with an AI assistant (Kimi). The author owns the product design, real-world testing, and all technical decisions.
