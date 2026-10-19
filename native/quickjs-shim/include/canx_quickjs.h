#ifndef CANX_QUICKJS_H
#define CANX_QUICKJS_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(CANX_QUICKJS_STATIC)
#    define CANX_QUICKJS_API
#  elif defined(CANX_QUICKJS_BUILDING)
#    define CANX_QUICKJS_API __declspec(dllexport)
#  else
#    define CANX_QUICKJS_API __declspec(dllimport)
#  endif
#  define CANX_QUICKJS_CALL __cdecl
#else
#  define CANX_QUICKJS_API __attribute__((visibility("default")))
#  define CANX_QUICKJS_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define CANX_QUICKJS_ABI_VERSION 2u

typedef struct canx_quickjs_runtime canx_quickjs_runtime;

typedef void(CANX_QUICKJS_CALL* canx_quickjs_tool_callback)(
    void* callback_context,
    uint64_t request_id,
    const char* tool_name,
    const char* input_json);

CANX_QUICKJS_API uint32_t CANX_QUICKJS_CALL canx_quickjs_abi_version(void);

CANX_QUICKJS_API int32_t CANX_QUICKJS_CALL canx_quickjs_create(
    size_t memory_limit,
    size_t stack_limit,
    canx_quickjs_tool_callback tool_callback,
    void* callback_context,
    canx_quickjs_runtime** out_runtime);

CANX_QUICKJS_API void CANX_QUICKJS_CALL canx_quickjs_destroy(
    canx_quickjs_runtime* runtime);

CANX_QUICKJS_API int32_t CANX_QUICKJS_CALL canx_quickjs_add_module(
    canx_quickjs_runtime* runtime,
    const char* module_name,
    const char* source);

CANX_QUICKJS_API int32_t CANX_QUICKJS_CALL canx_quickjs_eval(
    canx_quickjs_runtime* runtime,
    const char* source,
    const char* filename,
    int32_t as_module,
    uint32_t timeout_ms);

CANX_QUICKJS_API int32_t CANX_QUICKJS_CALL canx_quickjs_resolve_tool(
    canx_quickjs_runtime* runtime,
    uint64_t request_id,
    const char* result_json,
    int32_t is_error);

CANX_QUICKJS_API int32_t CANX_QUICKJS_CALL canx_quickjs_pump_jobs(
    canx_quickjs_runtime* runtime,
    int32_t max_jobs,
    uint32_t timeout_ms,
    int32_t* out_executed_jobs);

CANX_QUICKJS_API int32_t CANX_QUICKJS_CALL canx_quickjs_get_global_string(
    canx_quickjs_runtime* runtime,
    const char* property_name,
    char* output,
    size_t output_size,
    size_t* required_size);

CANX_QUICKJS_API const char* CANX_QUICKJS_CALL canx_quickjs_last_error(
    canx_quickjs_runtime* runtime);

#ifdef __cplusplus
}
#endif

#endif
