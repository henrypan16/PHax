# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

PHax: a Windows fax app (WinUI 3 desktop app + Windows Service) that talks to USB **Fax Class 1 / 1.0** modems
directly. The T.30 protocol, Class 1 AT driver and T.4/T.6 codecs are implemented in this repo (no Windows Fax
Service, no Class 2, no external fax engine).

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
- Network role: `NetworkConfig` (`network.json` in the data root, written by the installer through `PHax.Service.exe
  --configure-network/--pair`, or by Settings). **Server**: `Service/Remote/NetworkServerHost` runs Kestrel (HTTPS,
  self-signed certificate) only while `AllowClients` is on; `RemoteApi` is the API. **Client**: the service stays
  installed but `ModemSupervisor` leaves the port closed; the app talks to the server through `RemoteConnection` and
  `RemoteFaxData`, the client-side twins of `ServiceClient` and `LocalFaxData` behind `IServiceConnection`/`IFaxData`.
  The role is read once at app startup, so changing it restarts the app. Drafts and settings stay in the client's local
  database. Inbox is shared; outbound faxes belong to the submitting PC (`SubmittedByClientId`). Pairing is explained on
  `PairingProof`.
- `PHax.Modem`: `Serial/SerialPortTransport` (dedicated reader thread) → `Class1/ModemLink` (lines, DLE, AT history)
  → `Class1/Class1Modem` (T.31 primitives) → `T30/T30Sender`, `T30/T30Receiver` (state machines).
- `PHax.Imaging`: `T4Encoder`/`T4Decoder` (MH, MR, T.6), `TiffFax`, `PdfFax`, `DocumentRasterizer` (PDF via
  Windows.Data.Pdf, images via GDI+, Word via Office COM/LibreOffice).
- App-only features (need the user's session): toasts, progress popup, tray icon, single instance, scanning.
  Service-side: auto-print, backups, redial.
- Scanning: `PHax.App/Services/ScannerService` wraps NAPS2.Sdk (LGPL; keep its DLLs separate, unmodified, and listed
  in NOTICE). WIA in-process; TWAIN only via NAPS2's x86 worker (`NAPS2.Worker.exe`, x64 build only), because the
  drivers are 32-bit and in-process 64-bit TWAIN hung without a 64-bit TWAINDSM. Scans become fax TIFFs via `Bilevel`
  and are added as ordinary attachments. The dev PC has no scanner, so scanning is verified only on the fax PCs.
- Integrations (editions): `PHax.Core/Integrations`. The app loads `PHax.Integration.*.dll` from its folder at startup
  (`IntegrationLoader`); integrations implement `IAppIntegration` and use `IFaxSubmitter`/`IUserNotifier`; their
  settings live in `AppSettings.Integration` (free-form keys, preserved by builds without them). Integration code lives
  in separate private repos next to this one; `build.ps1 -Edition <Name>` builds `..\PHax.<Name>` into the app and
  names the installer `PHax-<Name>-Setup-<version>.exe`. Keep this repo vendor-neutral.

## Invariants — do not break

- Never mark a fax **Sent** unless the receiver answered MCF/RTP to EOP for the last page
  (`OutboundProcessor.CompleteAsync` double-checks `DeliveryConfirmed`). Page data without confirmation →
  `Uncertain`, which is never retried automatically.
- Never fall back to Class 2 commands; unsupported modems must be reported as such.
- Never silently substitute the simulator for a real modem; simulated items are flagged and the UI shows a banner.
- Do not log page contents.
- Uploads from clients are untrusted: `TiffFax.ValidateUpload` before `Read`, and keep them in memory rather than in
  the service's temp folder. Client ids for faxes make uploads idempotent, so a lost response can never dial twice.
- The network listener must never stop the service: exceptions end in `NetworkServerState.Error`, not in the host.
- Only events in `RemoteApi.SharedEvents` reach clients, and job events only reach the PC that owns the job.

## Hardware and testing realities

- **The development PC has no modem or COM ports.** Real-modem behaviour is only observed on the user's fax PCs
  (Conexant CX93001 on COM3) via installer builds; they send back _Settings → Diagnostics → Export logs_.
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
- The installed PHax may be running on the dev PC. The pipe name and single-instance mutex are fixed, so a second
  service or app would reach (or be blocked by) the installed one, whatever `PHAX_DATA_ROOT` says.
- `RemoteServerTests` run the real host and client over TLS on loopback, but not the WinUI app; check client-mode UI
  changes in the app too. `WebApplication.CreateSlimBuilder` has no `regex` route constraint: it fails on the first request.

## Conventions and gotchas

- C# latest, nullable on, file-scoped namespaces, primary constructors where the surrounding code uses them;
  match the existing comment density (XML doc on public types, short "why" comments).
- WinUI XAML: `x:Bind` everywhere; the XAML compiler cannot generate setters for positional records with init
  properties (use classes for items shown in templates); DataTemplates need `x:DataType`.
- Do not edit C# containing `'\r'`, `'\n'` or backslash paths through shell heredocs/Python string literals —
  escapes get mangled. Use the Edit/Write tools, or a script file.
- Commit only when the user asks.
