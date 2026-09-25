# NCM Converter

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Python](https://img.shields.io/badge/python-不需要-red)](README.md)
[![Runtime](https://img.shields.io/badge/runtime-.NET%20Framework%204.8-blue.svg)](https://dotnet.microsoft.com/download/dotnet-framework)

**网易云音乐 NCM 格式解密转换工具** —— 把 `.ncm` 还原成原始音频（MP3/FLAC/WAV/OGG），纯解密不重新编码，**音质完全不变**。

> **不用装 Python，不用装依赖，不用联网。**
> 这个版本是纯 C# / WinForms 写的独立 exe，跑在 Windows 自带的 .NET Framework 上。

---

## 🚀 开始使用

**第一步：** 下载本仓库（右上角 `<> Code` → `Download ZIP`），解压到任意文件夹。

**第二步：** 双击 **`NCMConverter.exe`**，把 `.ncm` 文件拖进窗口。

就这样。没有第三步。

<details>
<summary>点一下看看系统要求</summary>

- 系统：Windows 7 SP1 / 8 / 10 / 11
- 运行时：.NET Framework 4.8（Windows 10 和 11 **自带**，无需安装；Win7 若从未装过则需 [下载一次](https://dotnet.microsoft.com/download/dotnet-framework/net48)）
- 其他：**什么都不要**。不用 Python、不用 pip、不用 Visual Studio。

如果不确定有没有 .NET Framework：按 `Win + R`，输入 `appwiz.cpl` 回车，看列表里有没有 "Microsoft .NET Framework 4.x"。

</details>

---

## ✨ 两种用法

### 🖱️ 图形界面（默认）

双击 `NCMConverter.exe` 就打开界面：

- **拖拽** `.ncm` / `.flac` / `.wav` 文件进来，立即开始处理
- 也可以拖**整个文件夹**进来，会自动递归找 `.ncm`
- 「选择文件」/「选择文件夹」按钮做同样的事
- 可指定输出目录（留空 = 与源文件同目录）
- 可勾选：提取封面图、覆盖同名文件、FLAC 自动转 MP3

### ⌨️ 命令行（批量/脚本用）

在程序所在目录打开 cmd 或 PowerShell：

```bat
:: 单文件
NCMConverter.exe song.ncm

:: 整个文件夹（递归）
NCMConverter.exe D:\Music\NCM\ -r

:: 指定输出目录
NCMConverter.exe *.ncm -o D:\Music\MP3\

:: 递归 + 提取封面
NCMConverter.exe D:\Music\ -r --cover

:: 只看会处理哪些文件，不动手
NCMConverter.exe D:\Music\ --dry-run
```

| 参数 | 说明 |
|------|------|
| `-o, --output <目录>` | 输出目录（默认与源文件同目录） |
| `-f, --force` | 覆盖已存在的输出文件 |
| `-r, --recursive` | 递归搜索子文件夹 |
| `--cover` | 同时导出封面图 |
| `--to-mp3` | 解密后若是 FLAC/WAV/OGG，转 MP3（320k，需 ffmpeg） |
| `--dry-run` | 只列出将要处理的文件 |
| `-q, --quiet` | 静默模式 |
| `-v, --version` / `-h, --help` | 版本 / 帮助 |

退出码：`0` 全部成功，`1` 部分成功，`2` 有失败或跳过。

---

## 📦 关于 FLAC → MP3 转码

**NCM 解密本身零外部依赖**，出来的就是无损原始音频（通常是 MP3 或 FLAC）。

只有下面两种情况需要 ffmpeg（一个额外的 exe，不是 Windows 自带的）：

1. 勾选了「FLAC 自动转 MP3」
2. 直接拖入 `.flac` / `.wav` 文件

做法：下载 [ffmpeg.exe](https://www.gyan.dev/ffmpeg/builds/)（选 `ffmpeg-release-essentials.zip`），把里面的 `ffmpeg.exe` 放到 `NCMConverter.exe` **同一个文件夹**即可。也可以装到系统 PATH 里。

找不到 ffmpeg 时，界面上的复选框会自动置灰显示「缺少 ffmpeg」，命令行则提示一句后继续，不会崩。

---

## 🔨 自己编译（可选）

exe 已经打好了，直接能用。如果你想改代码重新编译：

**方式一：把 `src\NCMConverter.cs` 丢进 Visual Studio 编译**

**方式二：不用装任何东西，双击 `build.bat`**

`build.bat` 会找到 Windows 系统自带的 C# 编译器
（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）直接编译，产出 `NCMConverter.exe`。
全程离线、零依赖。

> 源码只用了 C# 5.0 语法，保证能被系统自带的旧版编译器处理。

---

## 📖 解密原理

NCM 文件 = 加密元数据 + 加密音频流。本工具做的是纯解密还原，不重新编码：

```
NCM 文件结构:
┌────────────────────────────────────┐
│ [8B]  Magic: CTENFDAM              │
│ [2B]  Gap: 0x0170                  │
│ [4B]  Key len (LE)                 │
│ [*B]  Key block → XOR 0x64        │
│               → AES-128-ECB        │  → RC4 音频密钥
│ [4B]  Meta len (LE)                │
│ [*B]  Metadata → XOR 0x63         │
│               → Base64 → AES-ECB   │  → 歌曲信息 JSON
│ [4B]  Cover CRC                    │
│ [5B]  Gap                          │
│ [4B]  Cover len (LE)               │
│ [*B]  Cover image (PNG/JPG 原文)   │
│ [*B]  Audio → 变体 RC4            │  → 原始 MP3/FLAC
└────────────────────────────────────┘
```

其中音频用的"变体 RC4"：标准的 RC4 KSA 生成 S-Box，但 PRGA 阶段 j 不累积、S-Box 不交换，
所以**密钥流以 256 字节为周期循环**——先算好一张 256 字节的表，后面就是对无穷长的音频流做一次 XOR，
速度很快，也不需要把整个文件读进内存。

**关键：解密后直接写出原始字节，不重新编码，音质无损。**

---

## 📁 文件说明

| 文件 | 用途 |
|------|------|
| `NCMConverter.exe` | 主程序，双击即用（已编译好） |
| `src/NCMConverter.cs` | 全部源码：解密内核 + GUI + CLI |
| `build.bat` | 用 Windows 自带编译器重新编译 |
| `README.md` | 就是这个文件 |
| `LICENSE` | MIT 开源协议 |
| `legacy-python/` | 最初的 Python 实现，保留作参考，不推荐使用 |

想把 MP3 转码能力加上，就把 `ffmpeg.exe` 放在根目录。

---

## ❓ 常见问题

**Q：转换后文件在哪？**
A：默认和源 `.ncm` 文件在同一个文件夹。可以在界面底部或 `-o` 参数指定别的地方。

**Q：文件名怎么来的？**
A：`歌手 - 歌名.扩展名`，取自 NCM 里的元数据；元数据缺失或乱码时回退到原文件名。
多位歌手用 `、` 连接。Windows 不允许的字符（`/ \ : * ? " < > |`）会换成 `_`。

**Q：扩展名是 .ncm 但提示"不是有效的 NCM 文件"？**
A：说明这个文件不是网易云音乐的格式（可能是改名过的普通音频，或者已经解密过一遍了）。

**Q：为什么下载的 exe 被浏览器/杀软拦了？**
A：exe 是新编译的可执行文件，没有数字签名，部分安全软件会误报。信任或不放心就自己跑 `build.bat` 编译一遍——源码全在这儿。

---

## 🙏 致谢

解密算法参考了：
- [taurusxin/ncmdump](https://github.com/taurusxin/ncmdump) (C++)
- [allenfrostline/pyNCMDUMP](https://github.com/allenfrostline/pyNCMDUMP) (Python)

---

## ⚠️ 免责声明

本工具仅供学习和研究使用。请尊重版权，仅转换您合法拥有的音乐文件。
