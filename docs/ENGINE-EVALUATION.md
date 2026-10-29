# Class 1 fax engine evaluation

PHax must drive EIA-578 / ITU-T T.31 Class 1 modems directly on Windows, without the Windows Fax Service, and only the PHax Windows Service may own the modem port. This note records the candidates considered and why PHax uses its own managed engine.

## Candidates

| Engine | Licence | Class 1 | Windows | Notes |
|---|---|---|---|---|
| **efax** (Ed Casas; efax-gtk fork) | GPL-2 | Yes (send + receive, no ECM) | No: POSIX termios, `fork`, lock files | Runs only under Cygwin/MSYS2 as an external process that opens the COM port itself. Status is reported through log text. |
| **HylaFAX** `faxgetty`/`faxsend` (Class1 driver) | BSD-style | Yes, mature, many modem quirks handled, ECM | No: a Unix daemon suite (faxq, faxgetty, hfaxd, sendmail integration) | The best reference for real-world Class 1 behaviour, but not portable to a Windows service in practice. |
| **SpanDSP** | LGPL-2.1 | Has T.30 and T.4/T.6, but its T.31 is the *modem emulator* (the DCE side for soft modems), not a driver for hardware modems | Builds on Windows | Would need a Class 1 DTE driver written anyway. Its T.30 expects audio/T.38 transport, not a hardware Class 1 DCE. |
| **mgetty+sendfax** | GPL | No: Class 2/2.0 only | No | Excluded (Class 2). |
| Windows Fax Service / FaxComEx | — | — | Yes | Excluded by requirement. |

## Decision

PHax implements the T.30 / T.31 / T.4 stack in managed C# (`PHax.Modem`, `PHax.Imaging`). It follows HylaFAX's and efax's Class 1 conventions where the specifications leave room:

- frame octets are in Class 1 order (T.30 bit 1 = LSB), so FCFs match HylaFAX's values (DIS 0x80, DCS 0x82|X, MCF 0x8C, …);
- received HDLC frames include the two FCS octets, and the modem's OK/ERROR is the FCS verdict;
- `+FRH`/`+FRM` are aborted with CAN when no carrier arrives;
- `+FCERROR` on `+FRM` means V.21 was detected, and the frame is then read with `+FRH=3`;
- page data is LSB-first, with an EOL before every line, fill bits for the negotiated minimum scan time, and RTC at the end.

Why not efax as an external executable:
1. The PHax service must be the single COM-port owner. efax would need to own the port for the whole call, and ring detection, auto-answer and job scheduling would then be split across two processes and a Cygwin layer.
2. Confirmation semantics would come from parsing log text and exit codes, when PHax needs exact per-page MCF/RTN accounting to avoid duplicate faxes.
3. GPL distribution obligations would apply to the bundled efax binary and Cygwin runtime.

efax remains useful as an **independent cross-check during hardware bring-up**: if a modem misbehaves with PHax, running `efax` under Cygwin on the same modem helps separate modem problems from engine problems.

## What the managed engine covers

- T.31 primitives: `+FCLASS`, `+FTH/+FRH=3` with DLE shielding, `+FTM/+FRM` (V.27ter, V.29, V.17 long and short train), `+FTS/+FRS`, `ATD`/`ATA`/`ATH`, abort.
- T.30 phases A–E for transmit and receive (non-ECM): DIS/DTC/CSI/NSF parsing, TSI/DCS, TCF generation and evaluation, CFR/FTT with speed fallback, MPS/EOM/EOP, MCF/RTP/RTN/PIP/PIN, CRP, DCN, T1/T2/T4 timers, three-try command repetition, recovery from lost responses (repeated post-message commands are answered without duplicating pages).
- T.4 MH and MR encode/decode with error concealment and bad-line counting; T.6 for storage (TIFF/PDF).
- Conservative outcome classification: success only after MCF/RTP to EOP; page data without confirmation → "unconfirmed".

Not implemented: ECM (T.30 Annex A), T.6 over the line, polling (DTC), V.34/V.8, B4/A3 widths, subaddressing (SUB/SID), and voice requests (PIP/PIN are treated as page accepted/rejected). See the README for the full list of limitations.
