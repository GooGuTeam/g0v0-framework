// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#include <cstring>
#include <string>
#include "cpu_features_macros.h"
#if defined(CPU_FEATURES_ARCH_X86)
#include "cpuinfo_x86.h"
#elif defined(CPU_FEATURES_ARCH_AARCH64)
#include "cpuinfo_aarch64.h"
#endif
#if defined(_WIN32)
#define API extern "C" __declspec(dllexport)
#else
#define API extern "C" __attribute__((visibility("default")))
#endif
// UTF-8 text in caller-owned storage. No native bitfield layouts cross the ABI.
API int g0_cpu_diagnostics(char* output, size_t capacity) noexcept {
    if (!output || !capacity) return 0;
    output[0] = 0;
    try {
        std::string value = "cpu_features: ";
        value.reserve(512);
#if defined(CPU_FEATURES_ARCH_X86)
        auto info = cpu_features::GetX86Info();
        value += "vendor=";
        value += info.vendor;
        value += ", brand=";
        value += info.brand_string;
        value += ", family=";
        value += std::to_string(info.family);
        value += ", model=";
        value += std::to_string(info.model);
        value += ", stepping=";
        value += std::to_string(info.stepping);
        value += ", features=";
        for (int i = 0; i < cpu_features::X86_LAST_; ++i) {
            auto feature = static_cast<cpu_features::X86FeaturesEnum>(i);
            if (cpu_features::GetX86FeaturesEnumValue(&info.features, feature)) {
                value += cpu_features::GetX86FeaturesEnumName(feature);
                value += ' ';
            }
        }
#elif defined(CPU_FEATURES_ARCH_AARCH64)
        auto info = cpu_features::GetAarch64Info();
        value += "architecture=aarch64, implementer=";
        value += std::to_string(info.implementer);
        value += ", part=";
        value += std::to_string(info.part);
        value += ", revision=";
        value += std::to_string(info.revision);
        value += ", features=";
        for (int i = 0; i < cpu_features::AARCH64_LAST_; ++i) {
            auto feature = static_cast<cpu_features::Aarch64FeaturesEnum>(i);
            if (cpu_features::GetAarch64FeaturesEnumValue(&info.features, feature)) {
                value += cpu_features::GetAarch64FeaturesEnumName(feature);
                value += ' ';
            }
        }
#else
        value += "diagnostic architecture not supported by this wrapper";
#endif
        if (value.size() >= capacity) return 0;
        memcpy(output, value.c_str(), value.size() + 1);
        return 1;
    } catch (...) { return 0; }
}
