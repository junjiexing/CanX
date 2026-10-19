cmake_minimum_required(VERSION 3.20)
project(canx_quickjs_shim VERSION 0.1.0 LANGUAGES C CXX)

set(QUICKJS_SOURCE_DIR "" CACHE PATH "Directory containing quickjs.h and quickjs.c")
set(CANX_QUICKJS_LIBRARY_TYPE "SHARED" CACHE STRING
  "Library type for the CanX QuickJS ABI shim (STATIC or SHARED)")
set_property(CACHE CANX_QUICKJS_LIBRARY_TYPE PROPERTY STRINGS STATIC SHARED)

if(NOT CANX_QUICKJS_LIBRARY_TYPE MATCHES "^(STATIC|SHARED)$")
  message(FATAL_ERROR "CANX_QUICKJS_LIBRARY_TYPE must be STATIC or SHARED")
endif()

if(NOT EXISTS "${QUICKJS_SOURCE_DIR}/quickjs.h")
  message(FATAL_ERROR "QUICKJS_SOURCE_DIR must contain quickjs.h")
endif()

add_library(quickjs_vendor OBJECT
  "${QUICKJS_SOURCE_DIR}/quickjs.c"
  "${QUICKJS_SOURCE_DIR}/dtoa.c"
  "${QUICKJS_SOURCE_DIR}/libregexp.c"
  "${QUICKJS_SOURCE_DIR}/libunicode.c"
  "${QUICKJS_SOURCE_DIR}/cutils.c")
target_compile_features(quickjs_vendor PRIVATE c_std_11)
target_compile_definitions(quickjs_vendor PRIVATE
  CONFIG_VERSION="2025-09-13"
  # libmpv may be built with MuJS, whose public allocator names collide with
  # QuickJS when both archives are linked into one NativeAOT executable.
  js_malloc=canx_qjs_malloc
  js_free=canx_qjs_free
  js_realloc=canx_qjs_realloc
  js_strdup=canx_qjs_strdup)
if(NOT WIN32)
  target_compile_definitions(quickjs_vendor PRIVATE _GNU_SOURCE)
endif()
if(MSVC)
  target_compile_options(quickjs_vendor PRIVATE /utf-8 /wd4018 /wd4146 /wd4244 /wd4267)
  if(CMAKE_C_COMPILER_ID STREQUAL "Clang")
    target_compile_options(quickjs_vendor PRIVATE /U__SIZEOF_INT128__ /clang:-fwrapv)
  endif()
elseif(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(quickjs_vendor PRIVATE
    -fwrapv
    -Wno-array-bounds
    -Wno-format-truncation
    -Wno-infinite-recursion)
endif()
target_include_directories(quickjs_vendor PRIVATE "${QUICKJS_SOURCE_DIR}")
set_target_properties(quickjs_vendor PROPERTIES POSITION_INDEPENDENT_CODE YES)

add_library(canx_quickjs ${CANX_QUICKJS_LIBRARY_TYPE}
  src/canx_quickjs.cpp
  $<TARGET_OBJECTS:quickjs_vendor>)
target_compile_features(canx_quickjs PRIVATE cxx_std_20)
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/quickjs-api")
configure_file("${QUICKJS_SOURCE_DIR}/quickjs.h"
  "${CMAKE_CURRENT_BINARY_DIR}/quickjs-api/quickjs.h" COPYONLY)
target_include_directories(canx_quickjs
  PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>")
target_include_directories(canx_quickjs SYSTEM PRIVATE
  "${CMAKE_CURRENT_BINARY_DIR}/quickjs-api")
if(NOT WIN32)
  target_link_libraries(canx_quickjs PRIVATE m dl pthread)
endif()
set_target_properties(canx_quickjs PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES)

if(CANX_QUICKJS_LIBRARY_TYPE STREQUAL "STATIC")
  target_compile_definitions(canx_quickjs PUBLIC CANX_QUICKJS_STATIC)
else()
  target_compile_definitions(canx_quickjs PRIVATE CANX_QUICKJS_BUILDING)
endif()

if(MSVC)
  target_compile_options(canx_quickjs PRIVATE /utf-8 /W4 /WX)
  if(CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
    target_compile_options(canx_quickjs PRIVATE /U__SIZEOF_INT128__)
  endif()
elseif(CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU")
  target_compile_options(canx_quickjs PRIVATE -Wall -Wextra -Werror)
endif()
