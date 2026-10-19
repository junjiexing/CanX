cmake_minimum_required(VERSION 3.20)
project(canx_aria2_shim VERSION 0.1.0 LANGUAGES CXX)

set(ARIA2_INCLUDE_DIR "" CACHE PATH "Directory containing aria2/aria2.h")
set(ARIA2_LIBRARY "" CACHE FILEPATH "Path to the libaria2 library")
set(ARIA2_DEPENDENCIES "" CACHE STRING
  "Additional libraries required when linking a static libaria2 archive")
set(CANX_WINDOWS_NATIVE_INCLUDE_DIR "" CACHE PATH
  "Windows static dependency include root (HarfBuzz and FreeType)")
set(CANX_ARIA2_LIBRARY_TYPE "SHARED" CACHE STRING
  "Library type for the CanX aria2 ABI shim (STATIC or SHARED)")
option(CANX_ARIA2_EXTENDED_API
  "Use CanX option/peer/server APIs from the pinned aria2 fork" OFF)
option(CANX_ARIA2_BUILD_ANDROID_SMOKE
  "Build the Android command-line ABI smoke test" OFF)
set_property(CACHE CANX_ARIA2_LIBRARY_TYPE PROPERTY STRINGS STATIC SHARED)

if(NOT CANX_ARIA2_LIBRARY_TYPE MATCHES "^(STATIC|SHARED)$")
  message(FATAL_ERROR "CANX_ARIA2_LIBRARY_TYPE must be STATIC or SHARED")
endif()

if(NOT EXISTS "${ARIA2_INCLUDE_DIR}/aria2/aria2.h")
  message(FATAL_ERROR "ARIA2_INCLUDE_DIR must contain aria2/aria2.h")
endif()

if(NOT EXISTS "${ARIA2_LIBRARY}")
  message(FATAL_ERROR "ARIA2_LIBRARY must point to libaria2")
endif()

add_library(canx_aria2 ${CANX_ARIA2_LIBRARY_TYPE} src/canx_aria2.cpp)
if(WIN32)
  target_sources(canx_aria2 PRIVATE
    ../windows-msvc-compat/src/getopt.cpp
    ../windows-msvc-compat/src/harfbuzz_freetype.cpp)
  if(NOT EXISTS "${CANX_WINDOWS_NATIVE_INCLUDE_DIR}/harfbuzz/hb.h")
    message(FATAL_ERROR
      "CANX_WINDOWS_NATIVE_INCLUDE_DIR must contain harfbuzz/hb.h on Windows")
  endif()
  target_include_directories(canx_aria2 PRIVATE
    "${CANX_WINDOWS_NATIVE_INCLUDE_DIR}")
endif()
target_compile_features(canx_aria2 PRIVATE cxx_std_17)
target_include_directories(canx_aria2
  PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
  PRIVATE "${ARIA2_INCLUDE_DIR}")
if(ARIA2_DEPENDENCIES AND NOT APPLE AND CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_link_libraries(canx_aria2 PRIVATE
    "-Wl,--start-group"
    "${ARIA2_LIBRARY}"
    ${ARIA2_DEPENDENCIES}
    "-Wl,--end-group")
else()
  target_link_libraries(canx_aria2 PRIVATE
    "${ARIA2_LIBRARY}"
    ${ARIA2_DEPENDENCIES})
endif()
if(CANX_ARIA2_EXTENDED_API)
  target_compile_definitions(canx_aria2 PRIVATE CANX_ARIA2_EXTENDED_API=1)
endif()
set_target_properties(canx_aria2 PROPERTIES
  POSITION_INDEPENDENT_CODE YES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES)

if(CANX_ARIA2_LIBRARY_TYPE STREQUAL "STATIC")
  target_compile_definitions(canx_aria2 PUBLIC CANX_ARIA2_STATIC)
else()
  target_compile_definitions(canx_aria2 PRIVATE CANX_ARIA2_BUILDING)
  if(NOT APPLE AND CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
    target_link_options(canx_aria2 PRIVATE
      "LINKER:--no-undefined"
      "LINKER:--exclude-libs,ALL")
  endif()
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(canx_aria2 PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

if(CANX_ARIA2_BUILD_ANDROID_SMOKE)
  if(NOT ANDROID)
    message(FATAL_ERROR "CANX_ARIA2_BUILD_ANDROID_SMOKE is only supported for Android")
  endif()
  add_executable(canx_aria2_android_smoke tests/android_smoke.cpp)
  target_compile_features(canx_aria2_android_smoke PRIVATE cxx_std_17)
  target_link_libraries(canx_aria2_android_smoke PRIVATE canx_aria2)
endif()
