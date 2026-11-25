# P59 fuel-pump relay control evidence

Supports OS 12587603 and 12587811. This corrects the old guessed CPID $02 preview.
The verified request payload is CPID $01, with bit 2 in data bytes 3 and 4:

| Operation | VPW request without CRC |
| --- | --- |
| Pump on | 6C 10 F0 AE 01 00 00 04 04 00 00 |
| Pump off | 6C 10 F0 AE 01 00 00 04 00 00 00 |
| Release | 6C 10 F0 AE 00 00 00 00 00 00 00 |

Primary reference: https://github.com/LegacyNsfw/12587603 at
5a563297204add19d43f8e371ffd90aacf900357,
`12587603-2004-Corvette-M6.sanitized.asm`.
CPID $01 in sub_3D7B0 copies its six bytes to FFFFA7FC..FFFFA801.
loc_3D12E checks bit 2 at FFFFA7FE and FFFFA7FF, enabling the pump override
at FFFFA7DD bit 2 and state FFFFA7E3 bit 6. sub_348C6 uses those exact bits
before its normal fuel-pump prime/run decisions. CPID zero calls sub_3E4B4,
clearing overrides and the pump timer flag FFFFA816.

Independently checked against the user's stock 12587811 1 MiB image, SHA256
2065c0ea922556357beb03adc652ba434d2b551ccc658eab03ee732dc63fe003.
0x41E1C..0x41E4E copies group-one bytes to FFFFABE8..FFFFABED;
0x415B2 checks bit 2 at FFFFABEA/FFFFABEB and sets FFFFABCB bit 2 and
FFFFABD1 bit 6. The pump routine at 0x34DA8 tests that enable/state pair.
This is source/firmware verification, not a physical relay observation.

The transport requires a supported OS, reported zero RPM and vehicle speed,
then validates PCM source, response service, CPID, success status E1, length
and any included VPW CRC. The pump test lasts two seconds. A CPID-zero
release is attempted even after a rejection, lost response or cancellation;
release failure is reported, never treated as confirmed automatic control.
CPID zero returns all PCM actuator overrides to automatic control.
