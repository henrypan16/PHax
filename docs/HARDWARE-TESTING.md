# Hardware validation

**Status: not yet performed.** No StarTech CX93010 or USRobotics USR5637 modem was attached during development. Every protocol test so far ran against the built-in simulated Class 1 modem. PHax makes no hardware-compatibility claim until the checklist below passes on real lines.

## Setup

1. Connect the modem's USB cable and the phone line (line jack, not phone jack). Note the COM port in Device Manager → Ports (COM & LPT) or Modems → Properties → Modem tab.
2. Stop the PHax service if it is running (`Stop-Service PHax`). Only one process can open the port.
3. Have a second fax endpoint available: a real fax machine, a second line, or an online fax-to-email number.

## Automated hardware tests

```powershell
$env:PHAX_HW_PORT    = "COM3"               # modem under test
$env:PHAX_HW_PROFILE = "startech-cx93010"   # or usr5637, generic-class1
$env:PHAX_HW_DIAL    = "5551234"            # a real fax machine (send test)
dotnet test PHax.Tests --filter "Category=Hardware"

$env:PHAX_HW_RECEIVE = "1"                  # then send a fax to the modem's line within 5 minutes
dotnet test PHax.Tests --filter "FullyQualifiedName~Hardware_ReceiveFax"
```

Each run writes a full protocol log (AT traffic and T.30 frames, no page content) to `%TEMP%\phax-hw-*.log`, and received pages to `%TEMP%\phax-hw-received-*.tif`.

## Checklist (per modem)

| # | Test | Pass criteria |
|---|---|---|
| 1 | Settings → Modem → Test connection | Class 1 or 1.0 listed; `+FTM`/`+FRM` include 24,48,72,96 (and 145/146 for V.17) |
| 2 | Send 1 page to a fax machine | Outbox → Sent with "MCF received"; the remote prints a legible page |
| 3 | Send 3 pages, mixed PDF/JPEG | All pages printed in order; protocol log shows MPS, MPS, EOP and three MCFs |
| 4 | Receive 1 page with auto-answer after 2 rings | Inbox entry; TIFF legible; the remote reports success |
| 5 | Receive 3+ pages, fine resolution | All pages present and the correct size |
| 6 | Busy number | Outbox shows a retry scheduled; no page data sent |
| 7 | Number that does not answer | "No answer" / "no fax answered"; retried per settings |
| 8 | Voice number that answers | No DIS → "no fax machine responded" |
| 9 | Pull the USB cable while idle | "Modem disconnected" notification; service reconnects when replugged |
| 10 | Pull the line during page transmission | Job marked **Unconfirmed**, not retried automatically |
| 11 | Manual answer (auto-receive off) | Incoming page shows ringing; Answer receives the fax |
| 12 | Maximum speed 9600 | DCS shows V.29/V.17 9600 in the protocol log |

Record modem firmware (`ATI3`, `+GMR`), the remote machine model and the logs for each run.

## Results log

| Date | Modem / firmware | Test | Result |
|---|---|---|---|
| 2026-10-29 | Conexant CX93001-EIS_V0.2013-V92 (StarTech), COM3, Class 1.0 | Receive 1 page (remote chose V.29 9600, fine, MR) | Page decoded with 2324 good / 0 bad lines; EOP answered MCF. The sender's final DCN arrived with a bad FCS, and PHax wrongly filed the fax as Partial. **Fixed**: after EOP/MCF the document is complete whether or not a valid DCN arrives. Retest pending. |

## Areas most likely to need tuning on real modems

These are handled per profile in `PHax.Modem/Profiles/ModemProfile.cs`:

- **TCF timing.** After DCS the receiver must issue `+FRM` within roughly 75 ms. USB latency can cause a missed TCF (receiver logs "No TCF received"). Look for `+FCERROR`/timeouts in the log after DCS.
- **`+FRM` result after carrier loss.** Some modems send `OK` instead of `NO CARRIER`; both are accepted.
- **FCS octets on `+FRH`.** If CSI/DIS appear two bytes too long or too short, toggle `HdlcReceiveIncludesFcs`.
- **V.17 short train.** If page 2 onward fails at 14400/12000, set `V17ShortTrainAfterFirstPage = false`, or cap `MaxBitRate` at 9600.
- **Flow control.** The USR5637 uses `AT&H1&R2`; the Conexant chip uses `AT&K3`. Underruns during `+FTM` corrupt pages (the remote answers RTN).
- **Turnaround.** If the remote misses our responses, enable `WaitForSilenceBeforeResponse` (`+FRS=7` before each response).
- **Caller ID.** `AT+VCID=1` (Conexant) or `AT#CID=1` (USR) only works if the line provides it.

Once a modem passes, set `HardwareVerified = true` in its profile and record firmware and date in `CompatibilityNotes`.
