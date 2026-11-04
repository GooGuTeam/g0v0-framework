# WASAPI native callback

`WasapiNativeBridge` writes a small machine-code thunk at runtime that BASS WASAPI calls to fetch audio. The thunk calls `BASS_ChannelGetData` on the mixer directly, without entering the CLR, so the audio callback is not delayed by garbage collection or thread transitions. It returns early (without calling BASS) when the context is null or the mixer handle is 0, which happens before the mixer exists and while the device is reinitialised.

There is one thunk per architecture:

| Architecture | Notes |
| --- | --- |
| x64 | Offsets, stack alignment and shadow space checked; used on hardware |
| ARM64 | The two `cbz` early-return branches use immediates 14 and 12. Earlier versions jumped to the BASS call instead of the return, calling BASS with invalid arguments and unwinding a frame that was never pushed |
| x86 | The two `jz` early-return branches target offset 40. Earlier versions targeted 38, inside `ret 12` |

The ARM64 and x86 thunks have been checked by decoding their bytes only; they have not run on hardware. When changing a thunk, recompute every branch offset from the final byte layout and decode the result.
