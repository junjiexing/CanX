cmake_minimum_required(VERSION 3.22)
project(CanXLua C)

set(LUA_SOURCE_DIR "" CACHE PATH "Pinned Lua 5.2 source checkout")
set(lua_sources
    lapi lcode lctype ldebug ldo ldump lfunc lgc llex lmem lobject
    lopcodes lparser lstate lstring ltable ltm lundump lvm lzio
    lauxlib lbaselib lbitlib lcorolib ldblib liolib lmathlib loslib
    lstrlib ltablib loadlib linit)
list(TRANSFORM lua_sources PREPEND "${LUA_SOURCE_DIR}/")
list(TRANSFORM lua_sources APPEND ".c")
add_library(lua STATIC ${lua_sources})
target_include_directories(lua PUBLIC "${LUA_SOURCE_DIR}")
if(WIN32)
    target_compile_definitions(lua PRIVATE LUA_USE_WINDOWS _CRT_SECURE_NO_WARNINGS)
endif()
install(TARGETS lua ARCHIVE DESTINATION lib)
install(FILES
    "${LUA_SOURCE_DIR}/lua.h" "${LUA_SOURCE_DIR}/luaconf.h"
    "${LUA_SOURCE_DIR}/lauxlib.h" "${LUA_SOURCE_DIR}/lualib.h"
    DESTINATION include)