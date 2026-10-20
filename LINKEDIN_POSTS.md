# 📱 CleanCore — LinkedIn Launch Posts

Two ready-to-publish post templates crafted for your launch:
1. **Option 1: Engaging Community / Indie Hacker Post** (Casual, punchy, story-driven)
2. **Option 2: High-Level Engineering / Architecture Post** (Technical, systems-focused)

---

## 🚀 Option 1: Engaging Community & Developer Post

```text
Ever wonder why standard Windows disk cleanup tools still feel like they were built in 1998? 🐢

I got tired of slow recursive directory crawlers freezing my system and bloated "system cleaners" bundled with tracking and upsells. So I built something much faster, completely private, and 100% open-source:

⚡ Introducing CleanCore — Advanced Windows Storage Intelligence & System Maintenance Platform.

Built natively on .NET 9 and WinUI 3 with direct Win32 NTFS Master File Table (MFT) interrogation, CleanCore analyzes over 1,200,000 files/sec on Gen4 NVMe drives without locking your UI.

Here is what it does out of the box:
📁 NTFS Storage Explorer: Sub-second volume capacity scans and interactive category treemaps.
🧹 Developer Cache Purge: Cleans abandoned node_modules, Visual Studio bin/obj folders, Rust Cargo targets, and browser caches safely.
🔍 Large Files Hunter: Instantly surfaces dormant ISOs, virtual disks (.vhdx), and stale archives.
⚙️ Windows DISM Servicing: Reclaims gigabytes inside C:\Windows\WinSxS using native CBS servicing.
🧬 Hardware SHA-256 Deduplicator: 3-tier byte-level duplicate analysis with zero false positives.
🤖 Offline AI Copilot: 100% private, on-device system log diagnostics via local Ollama (Phi-3 / Qwen) — zero cloud telemetry!
🛡️ Zero-Data-Loss Quarantine: Staged deletion with instant 1-click bit-for-bit rollback.

To celebrate the launch, I also built a fully interactive retro 3D CRT monitor & rotating VHS tape deck showcase where you can test the modules directly in your browser!

Try the interactive showcase or grab the standalone executable below:
🌐 Live Retro Showcase: https://cleancore-app.pages.dev
💻 Open Source GitHub: https://github.com/pali2410/cleanup
⤓ Direct Download: https://cleancore-app.pages.dev/download/CleanCore.exe

Star ⭐ the repo on GitHub if you find it useful or want to contribute!

#opensource #csharp #dotnet #windows11 #winui3 #desktopapp #softwareengineering #performance #github #developer
```

---

## 🏛️ Option 2: Professional Systems Engineering & Architecture Post

```text
Delighted to announce the open-source release of CleanCore — a high-performance Windows Storage Intelligence and Component Servicing platform engineered for Windows 10 & 11.

Traditional filesystem analysis utilities rely on synchronous, recursive Win32 directory traversals that incur massive kernel-to-user-mode context switching overhead and high I/O latency.

CleanCore takes an architectural approach focused on raw throughput, memory safety, and non-destructive maintenance:

Key Architectural Highlights:
🔹 Direct NTFS MFT Interrogation: Bypasses standard directory enumeration via low-level Win32 DeviceIoControl / FSCTL calls (FSCTL_GET_NTFS_VOLUME_DATA), streaming raw cluster data at > 1,200,000 files/sec.
🔹 Zero-Allocation Pipelines: Utilizes modern C# 13 System.Threading.Channels, ArrayPool<T>, and ReadOnlySpan<byte> to maintain a working memory footprint below 120 MB during multi-terabyte scans.
🔹 Native Windows Servicing (CBS & DISM): Interfaces with Windows Component-Based Servicing for safe WinSxS component store compression and superseded update removal with automated System Restore Point creation.
🔹 Three-Tier Cryptographic Deduplication: Combines fast size-filtering, 4KB header/footer hashing, and hardware-accelerated SHA-256 (AVX2 / ARM Crypto) with optional NTFS hardlinking.
🔹 Zero-Data-Loss Staging Vault: Employs an embedded SQLite WAL transaction ledger preserving original NTFS ACL security descriptors, timestamps, and hash manifests for deterministic 1-click rollback.
🔹 Air-Gapped On-Device AI: Integrates with local Ollama endpoints (Phi-3 Mini, Qwen 2.5) for offline log parsing and anomaly detection with zero telemetry or cloud dependencies.

Explore the complete open-source codebase, technical blueprints, and live interactive showcase:
📌 GitHub Repository: https://github.com/pali2410/cleanup
🌐 Interactive 3D Showcase: https://cleancore-app.pages.dev
📐 Architecture Blueprint: https://cleancore-app.pages.dev/profile
⤓ Standalone Binary: https://cleancore-app.pages.dev/download/CleanCore.exe

Feedback, code reviews, and pull requests are warmly welcomed!

#SoftwareEngineering #DotNet #SystemsProgramming #WindowsInternals #WinUI3 #OpenSource #PerformanceEngineering #SoftwareArchitecture #GitHub
```
