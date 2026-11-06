// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Simd's Windows CpuModel() calls the x86-only __cpuid intrinsic, which MSVC does not provide for ARM64.
// Force-included into the Simd sources on that target only. Reporting no extended leaves leaves the CPU model string empty;
// feature detection on ARM64 does not use it.
#pragma once
#include <intrin.h>

static inline void __cpuid(int cpuInfo[4], int)
{
    cpuInfo[0] = cpuInfo[1] = cpuInfo[2] = cpuInfo[3] = 0;
}
