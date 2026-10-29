# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

PHax: a Windows fax app (WinUI 3 desktop app + Windows Service) that talks to USB **Fax Class 1 / 1.0** modems
directly. The T.30 protocol, Class 1 AT driver and T.4/T.6 codecs are implemented in this repo (no Windows Fax
Service, no Class 2, no external fax engine). The project was called **FTIFax** until version 0.1.9; legacy
handling for that name is intentional (see "Rename" below).

## Commands

```powershell
dotnet build PHax.slnx -p:Platform=x64                     # the WinUI app needs an explicit platform
dotnet test PHax.Tests --filter "Category!=Hardware"       # ~80 tests, ~1 min
dotnet test PHax.Tests --filter "FullyQualifiedName~SimulationTests.SinglePage"   # one test
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 [-SkipTests]          # icon + tests + publish + installer
```

- Bump `<Version>` in `Directory.Build.props` for every installer handed to the user (installs upgrade in place).
- Installer: Inno Setup 6 (`installer/PHax.iss`), output `out\installer\PHax-Setup-<version>.exe`.
- Package versions are central: `Directory.Packages.props`.
- EF migrations: `dotnet ef migrations add <Name> --project PHax.Data --output-dir Migrations` (tool in `dotnet-tools.json`).

## Architecture

- `PHax.Service` is the **only** process that opens the COM port (`ModemSupervisor` loop: idle/RING, answer, send
  queue, probe, reconnect). The app talks to it over the named pipe `PHax.Service.v1` (newline-delimited JSON,
  `PHax.Core/Ipc/PipeProtocol.cs`). Both share SQLite (WAL) at `C:\ProgramData\PHax\phax.db`.
- `PHax.Modem`: `Serial/SerialPortTransport` (dedicated reader thread) → `Class1/ModemLink` (lines, DLE, AT history)
  → `Class1/Class1Modem` (T.31 primitives) → `T30/T30Sender`, `T30/T30Receiver` (state machines).
- `PHax.Imaging`: `T4Encoder`/`T4Decoder` (MH, MR, T.6), `TiffFax`, `PdfFax`, `DocumentRasterizer` (PDF via
  Windows.Data.Pdf, images via GDI+, Word via Office COM/LibreOffice).
- App-only features (need the user's session): toasts, progress popup, tray icon, single instance. Service-side: auto-print, backups, redial.

## Invariants — do not break

- Never mark a fax **Sent** unless the receiver answered MCF/RTP to EOP for the last page
  (`OutboundProcessor.CompleteAsync` double-checks `DeliveryConfirmed`). Page data without confirmation →
  `Uncertain`, which is never retried automatically.
- Never fall back to Class 2 commands; unsupported modems must be reported as such.
- Never silently substitute the simulator for a real modem; simulated items are flagged and the UI shows a banner.
- Do not log page contents.

## Hardware and testing realities

- **The development PC has no modem or COM ports.** Real-modem behaviour is only observed on the user's fax PCs
  (Conexant CX93001 on COM3) via installer builds; they send back *Settings → Diagnostics → Export logs*.
  Simulation tests do **not** exercise `SerialPortTransport` / `System.IO.Ports`.
- Treat changes to the serial layer as high risk. Past regressions: closing an idle port hung (async reads
  ignore timeouts), a failed init leaked the port handle ("COM3 in use" by the service itself), and
  `DiscardInBuffer` aborts the pending read (ERROR_OPERATION_ABORTED) which must not be treated as a disconnect.
- Real-line findings encoded in the engine: some modems pass the 0x7E flag in HDLC data; the final DCN often
  arrives with a bad FCS (document is complete after EOP+MCF); VoIP lines can pass V.17 TCF but garble V.17 page
  data — V.17 is tried once, then V.29 + MH.
- When adding a fix for a real-hardware log, add a simulation test reproducing it (`SimulatedLine.Faults`).
- Dev loop without hardware: run the service and app with `PHAX_DATA_ROOT=<scratch folder>` and enable the
  simulator in settings.

## Conventions and gotchas

- C# latest, nullable on, file-scoped namespaces, primary constructors where the surrounding code uses them;
  match the existing comment density (XML doc on public types, short "why" comments).
- WinUI XAML: `x:Bind` everywhere; the XAML compiler cannot generate setters for positional records with init
  properties (use classes for items shown in templates); DataTemplates need `x:DataType`.
- Do not edit C# containing `'\r'`, `'\n'` or backslash paths through shell heredocs/Python string literals —
  escapes get mangled. Use the Edit/Write tools, or a script file.
- Commit only when the user asks.

## Rename (FTIFax → PHax)

Installs made before 0.2.0 used the name FTIFax. Intentional legacy references:
`AppPaths.MigrateLegacyData` (moves `ProgramData\FTIFax`, renames `ftifax.db`), `StartupRegistration`/`AppNotifications`
(remove old registry entries), and `RemoveLegacyFTIFax` in the installer (removes the old service and runs the old
uninstaller silently, which keeps data).
