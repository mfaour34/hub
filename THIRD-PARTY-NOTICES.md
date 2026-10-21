# Third-party notices

Hub is licensed under the GNU General Public License v3.0 (see `LICENSE`).

## Included in the installer

| Component | Licence | Notes |
|---|---|---|
| [.NET 8 runtime](https://github.com/dotnet/runtime) | MIT | Bundled because Hub is self-contained, so no separate .NET install is needed. |
| [Microsoft.Web.WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2) | [Microsoft WebView2 SDK licence](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40/license) | Loader and .NET wrappers. The WebView2 *runtime* itself is part of Windows and is not bundled. |

**Additional permission (GPL-3.0 §7):** as a special exception, the copyright holders of Hub give permission to link Hub with the .NET runtime and the Microsoft WebView2 SDK, and to distribute the result, without those components having to be under the GPL.

## Code derived from other projects

| Where | From | Licence |
|---|---|---|
| `src/Hub.Core/Logitech/OnboardEq.cs` | [HeadsetControl](https://github.com/Sapd/HeadsetControl): the Centurion onboard-EQ payload and fixed-point coefficient layout | GPL-3.0 |

## Algorithms from the literature (no code copied)

- Biquad filters: R. Bristow-Johnson, *Audio EQ Cookbook*.
- Dynamic-sensitivity curve: PCHIP monotone cubic interpolation. F. N. Fritsch & R. E. Carlson, "Monotone Piecewise Cubic Interpolation", SIAM J. Numer. Anal. 17(2), 1980.
- Limiter (`native/hub-limiter`): a standard look-ahead peak limiter. D. Giannoulis, M. Massberg & J. D. Reiss, "Digital Dynamic Range Compressor Design — A Tutorial and Analysis", JAES 60(6), 2012.

## Trademarks

Razer, Synapse, Viper and HyperSpeed are trademarks of Razer Inc. Logitech, G HUB, PRO X and LIGHTSPEED are trademarks of Logitech. SteelSeries and Sonar are trademarks of SteelSeries ApS. VoiceMeeter is a trademark of VB-Audio Software. They are used here only to say which products Hub works with or can replace. Hub is an independent project, not affiliated with or endorsed by any of these companies, and ships none of their software, images or presets.
