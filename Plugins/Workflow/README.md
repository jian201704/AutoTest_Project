# Excel Workflow Plugin

`ExcelWorkflowCompiler` compiles a workbook's `StepTable` into ordinary OpenTAP test steps. Hardware resources are resolved by the row's `Resource` value from Bench > Instruments. `ResourceMap` may provide shared defaults such as `CANFD_ECU` and `DC_Power_Supply`; those entries are optional unless a step needs them. The `Remark` column is optional.

## Implemented Actions

| Excel Action | OpenTAP behavior |
| --- | --- |
| `send_uds` | Runs the existing CAN UDS request/response/decode step. Uses row `Resource`, or shared `CANFD_ECU` when empty. |
| `delay` | Waits `delay=<milliseconds>` (case-insensitive); the wait observes plan Abort. |
| `Ping_Test` | Runs Windows `ping.exe` with `-S SourceIP TargetIP`, four packets per attempt, up to five attempts, 1 s retry delay, zero packet loss and mean response strictly below 100 ms. Records loss and mean response. |
| `receive_can` | Receives raw CAN-FD frames on the named `CanInstrument`; passes only on exact identifier and payload equality. XPeng settings are 1 s receive timeout, ten receive attempts and ZLG receive mode 2. |
| `CANFD_OFF` | Closes the named CAN instrument, matching the legacy `CAN_UDS_Release` action. |
| `SOC_Flash` | Connects to XPU A and B, sends the profile handshake, requests and verifies `project=`/`version=`, starts both burns, drains startup responses, polls each status independently and records Pass/Fail/Free/Timeout. Both endpoints must pass. Commands, signatures, timeouts and delays are profile-driven. |
| `INST_DIO_G96_SET` | Sends the configured raw serial command plus CRLF, retries up to three times at 500 ms response timeout with 80 ms spacing, requires `TRUE`, then settles 500 ms. |
| `INST_DIO_M48_SET` | Same response/retry behavior as the source script, requiring `True`. |
| `INST_DIO_LEDBoard_SET` | Same response/retry behavior, requiring `OK`. |
| `INST_DMM_DMM6500_MEAS` | Configures a supported DMM function/range/NPLC, reads with `READ?`, rejects empty/over-range/non-numeric values, applies scale/offset and optional absolute current, then checks workbook limits. |
| `INST_SIG_DG1022_ON` | Turns output off, sets and verifies waveform, sets Vpp amplitude/offset/frequency/square duty, enables output and waits 2 s. |
| `INST_SIG_DG1022_OFF` | Sends only the channel output-off command, as the JTS implementation does for OFF rows. |
| `power_on`, `power_off` | Controls the existing generic SCPI power supply step. |
| `manual_check`, `run_command` | Preserves the existing operator dialog/measurement and structured ping command support. |

The compiler wraps rows using `FailurePolicy` as OpenTAP retry/break behavior. Step-level defaults above represent the XPeng dispatcher behavior; explicit instrument response timeouts are applied through the VISA I/O timeout.

## XPeng Resource Setup

`Plugins/Script/Examples/ExcelWorkflowSequence.csx` runs an ordered list of compiled workbooks and stops before the next workbook when the current plan verdict is not Pass. Set `AIUM_WORKBOOKS` to a semicolon-separated ordered list, for example `EOL.xlsx;APPDownload.xlsx;Final.xlsx`. Compile all workbooks before any plan starts, so a missing mapping or unsupported action fails before the first test is executed.

The current JTS main entry has `EOL_Test()` commented out; the sequence is therefore operator-configured, and adding the EOL workbook is an explicit choice. This script sequences workbook plans only. Barcode scanning, fixture cylinder/safety-optical interlocks, MES authorization, station-specific DUT power sequencing, the 70-second stabilization delay, current acceptance limits and cleanup/abort fixture reset still require dedicated steps. Scanner and fixture I/O are raw serial protocols, not SCPI.

Configure these resources in the OpenTAP Bench. The workbook has per-step resource names for the serial and measurement devices, but does not define them in its `ResourceMap`:

| Bench resource name | Instrument type | Connection/configuration |
| --- | --- | --- |
| `CANFD_ECU` | `CanInstrument` | UDS channel/device/IDs from the station configuration. |
| `LLCAN`, `RLCAN`, `WLCAN`, `LACAN`, `RACAN` | `CanInstrument` | Each must select the correct raw CAN-FD channel and bit timing. Confirm physical routing before running. |
| `DC_Power_Supply` | `ScpiPowerSupplyInstrument` | Required only if a workbook uses `power_on/off`. |
| `G96`, `M48`, `LEDBoard` | `SerialCommandInstrument` | Configure the physical COM port, baud rate, data bits, parity, stop bits and flow control. These are raw serial protocols, not SCPI; commands include their literal CRLF terminators and replies are collected until the configured idle timeout. |
| `Scanner`, `G96B_MainControl` | `SerialCommandInstrument` | The same raw serial transport can be shared by scanner/fixture-specific TestSteps. The scanner sends a binary/text trigger and reads barcode bytes; the fixture controller sends configured commands, checks the optical safety response and polls cylinder sensors. Dedicated scanner and fixture workflow steps are still required; they are not implemented by `SerialCommandStep`. |
| `DMM6500`, `DG1022` | `ScpiVisaInstrument` | Use station VISA addresses. The supplied INI lists DMM6500 `TCPIP0::192.168.1.103::inst0::INSTR` and DG1022 `USB0::0x0400::0x09C4::DG1D265001289::INSTR`; replace them with the target station's values. |
| `TCP` | `DualXpuTcpInstrument` | Configure the two MiniPC IP addresses and port 7057. Set SOC Protocol Profile to `Plugins/Workflow/Profiles/XPeng_AIUM_SocFlash.json`. |

The XPeng profile carries project `R02-DUAL`, version `0.4.3-r02-robot`, the 396-byte handshake frame extracted from the JTS source, all command/status signatures, response timeout, polling limit and timing. The sequence matches JTS: 100 ms after handshake receive; 1 s before starting after version checks; 1 s after start followed by response drains; 5 s before polling; at most 1000 polls at 1 s intervals; then a 1 s delay and final response drains. `OverallTimeoutMs: 0` disables the additional wall-clock limit so the 1000-poll limit matches JTS. The sample IPs in `Base_Config.ini` are station-specific and are intentionally not embedded in the profile.

`receive_can` resource names are not present in the workbook `ResourceMap`, so those named `CanInstrument` instances must be configured explicitly. Validate CAN ID interpretation, FD data length, sampling point and routing with the target ZLG device before production use.

## Build and Verification

Build `Plugins/Workflow/Tap.Plugins.Workflow.csproj`. The NUnit regression `CompilesXpEngWorkbooksAndMapsDeviceActions` imports the three XPeng workbooks with un-opened fake resource objects; it validates workbook parsing and step mapping only, not physical hardware behavior. `LoadsXpEngSocFlashProfileAndCompleteHandshakeFrame` validates profile loading and handshake completeness.