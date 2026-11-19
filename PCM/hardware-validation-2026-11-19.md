# P59 hardware validation — 2026-11-19

OS replacement from `1252075` to `12587811` completed successfully on Linux
using an OBDX Pro GT over USB and P59 hardware ID `12589162` with a 1 MiB
AMD AM29F800BB flash chip.

Backend source: commit `8f22f55384d9323059df9918874d1e59cb694253`.
Backend executable SHA-256:
`d4193cffcf5401922a5c8ce5094661fed9dae3616b388027a77279c750776f09`.

| Check | Observed result |
| --- | --- |
| Identification | VIN, OS, calibration ID, hardware ID, serial and voltage returned |
| Original full backup | 1,048,576 bytes; all eight image checksums valid |
| Independent backup verification | All 19 PCM flash-range CRCs matched the saved image |
| Preflight | Engine RPM 0; vehicle speed 0; PCM voltage 13.0 V |
| Simulated write | Passed at 4× VPW speed without erasing flash |
| OS/calibration/boot write | Passed at 4× VPW; all write requests succeeded on their first attempt |
| Written-range verification | All required CRC ranges matched; backend reported flash success |
| Post-write identification | OS 12587811; original VIN and serial retained |
| Independent final comparison | All 19 flash-range CRCs matched the target plus the original parameter blocks; zero mismatches |
| Parameter preservation | Both ranges, 0x004000–0x005FFF and 0x006000–0x007FFF, matched the original backup |

The initial large-packet receive failure was fixed with a managed serial
receive queue. The initial CRC timeout was fixed by continuing to receive
pending OBDX CRC replies before retransmitting. A physical PCM power cycle was
needed after the earlier CRC failure. No erase or flash was attempted before
the original backup passed independent CRC verification.

Offline validation on the laptop: 48 tests passed. The compiler-dependent
delayed-CRC test was skipped by unittest because the compiler was absent;
its separately compiled two-response harness passed under Mono. The serial
regression also received a complete 16 KiB frame through a pseudo-terminal.

This result covers this controller, adapter and OS replacement. It does not
establish recovery, full-clone, calibration-only, parameter-only, continuous
scanner streaming, DTC readback, other adapters, or engine-running behavior.
Original images and complete session logs are retained on the operator's USB.
