cmake_minimum_required(VERSION 3.24)
# Link the C++ runtime statically, so the library does not depend on the Visual C++ redistributable or the system libstdc++.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
project(g0v0-image-codecs LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
if(MSVC)
    # Third-party sources are UTF-8; without this, non-UTF-8 system code pages warn (C4819).
    add_compile_options(/utf-8)
endif()

set(WUFFS_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/wuffs")
set(STB_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/stb")
foreach(source IN ITEMS
        "${WUFFS_SOURCE_DIR}/release/c/wuffs-unsupported-snapshot.c"
        "${WUFFS_SOURCE_DIR}/LICENSE-MIT"
        "${STB_SOURCE_DIR}/stb_image_write.h"
        "${STB_SOURCE_DIR}/LICENSE")
    if(NOT EXISTS "${source}")
        message(FATAL_ERROR "Missing image codec submodule file: ${source}. Run git submodule update --init --recursive from the framework repository.")
    endif()
endforeach()
add_library(g0v0-image-codecs SHARED image_codecs.cpp)
include(simd-processing.cmake)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(BUILD_EXECUTABLE OFF CACHE BOOL "" FORCE)
set(ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
add_subdirectory(third_party/cpu_features EXCLUDE_FROM_ALL)
target_sources(g0v0-image-codecs PRIVATE cpu_diagnostics.cpp)
target_link_libraries(g0v0-image-codecs PRIVATE CpuFeatures::cpu_features)
install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/third_party/cpu_features/LICENSE" DESTINATION licences RENAME LICENCE-cpu_features)
target_include_directories(g0v0-image-codecs PRIVATE "${WUFFS_SOURCE_DIR}/release/c" "${STB_SOURCE_DIR}")
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    target_link_options(g0v0-image-codecs PRIVATE -static-libstdc++ -static-libgcc)
endif()
if(ANDROID)
    # Android 15+ devices may use 16 KB pages, and Android 16 requires libraries aligned for them.
    target_link_options(g0v0-image-codecs PRIVATE "-Wl,-z,max-page-size=16384")
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    # iOS apps load dynamic libraries only from embedded frameworks; osu.Framework.iOS resolves @rpath/g0v0-image-codecs.framework.
    set_target_properties(g0v0-image-codecs PROPERTIES
        FRAMEWORK TRUE
        MACOSX_FRAMEWORK_IDENTIFIER sh.g0v0.image-codecs
        MACOSX_FRAMEWORK_BUNDLE_VERSION 1
        MACOSX_FRAMEWORK_SHORT_VERSION_STRING 1.0
        INSTALL_NAME_DIR "@rpath")
endif()
install(TARGETS g0v0-image-codecs RUNTIME DESTINATION . LIBRARY DESTINATION . FRAMEWORK DESTINATION .)
install(FILES "${WUFFS_SOURCE_DIR}/LICENSE-MIT" DESTINATION licences RENAME LICENCE-Wuffs-MIT)
install(FILES "${STB_SOURCE_DIR}/LICENSE" DESTINATION licences RENAME LICENCE-stb)
