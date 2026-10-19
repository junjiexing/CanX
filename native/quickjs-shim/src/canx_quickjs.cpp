#include "canx_quickjs.h"

#include <quickjs.h>

#include <atomic>
#include <chrono>
#include <cstring>
#include <exception>
#include <memory>
#include <new>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

struct PendingToolCall {
  JSValue resolve;
  JSValue reject;
};

struct canx_quickjs_runtime {
  JSRuntime* runtime = nullptr;
  JSContext* context = nullptr;
  canx_quickjs_tool_callback tool_callback = nullptr;
  void* callback_context = nullptr;
  std::atomic<int64_t> deadline_nanoseconds{0};
  uint64_t next_request_id = 1;
  std::unordered_map<uint64_t, PendingToolCall> pending_calls;
  std::unordered_map<std::string, std::string> modules;
  std::vector<JSValue> pending_module_evaluations;
  std::string last_error;
};

namespace {

constexpr int32_t invalid_argument = -1000;
constexpr int32_t allocation_failure = -1001;
constexpr int32_t javascript_exception = -1002;
constexpr int32_t request_not_found = -1003;
constexpr int32_t buffer_too_small = -1004;
constexpr int32_t native_exception = -1005;

int64_t monotonic_nanoseconds() {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(
             std::chrono::steady_clock::now().time_since_epoch())
      .count();
}

void set_deadline(canx_quickjs_runtime* runtime, const uint32_t timeout_ms) {
  const auto deadline = timeout_ms == 0
                            ? int64_t{0}
                            : monotonic_nanoseconds() +
                                  static_cast<int64_t>(timeout_ms) * 1000000;
  runtime->deadline_nanoseconds.store(deadline, std::memory_order_relaxed);
}

int interrupt_handler(JSRuntime*, void* opaque) noexcept {
  auto* runtime = static_cast<canx_quickjs_runtime*>(opaque);
  const auto deadline =
      runtime->deadline_nanoseconds.load(std::memory_order_relaxed);
  return deadline != 0 && monotonic_nanoseconds() >= deadline ? 1 : 0;
}

void capture_exception(canx_quickjs_runtime* runtime, JSContext* context) {
  const auto exception = JS_GetException(context);
  const char* text = JS_ToCString(context, exception);
  runtime->last_error = text == nullptr ? "JavaScript exception"
                                        : std::string(text);
  if (text != nullptr) {
    JS_FreeCString(context, text);
  }
  JS_FreeValue(context, exception);
}

void capture_value(canx_quickjs_runtime* runtime, JSContext* context,
                   JSValueConst value) {
  const char* text = JS_ToCString(context, value);
  runtime->last_error = text == nullptr ? "JavaScript promise rejected"
                                        : std::string(text);
  if (text != nullptr) {
    JS_FreeCString(context, text);
  }
}

int32_t check_module_evaluations(canx_quickjs_runtime* runtime) {
  auto& evaluations = runtime->pending_module_evaluations;
  for (auto iterator = evaluations.begin(); iterator != evaluations.end();) {
    const auto state = JS_PromiseState(runtime->context, *iterator);
    if (state == JS_PROMISE_PENDING) {
      ++iterator;
      continue;
    }

    if (state == JS_PROMISE_REJECTED) {
      JSValue reason = JS_PromiseResult(runtime->context, *iterator);
      capture_value(runtime, runtime->context, reason);
      JS_FreeValue(runtime->context, reason);
      JS_FreeValue(runtime->context, *iterator);
      iterator = evaluations.erase(iterator);
      return javascript_exception;
    }

    JS_FreeValue(runtime->context, *iterator);
    iterator = evaluations.erase(iterator);
  }
  return int32_t{0};
}

JSValue invoke_tool(JSContext* context, JSValueConst, const int argument_count,
                    JSValueConst* arguments) {
  auto* runtime =
      static_cast<canx_quickjs_runtime*>(JS_GetContextOpaque(context));
  if (runtime == nullptr || argument_count != 2) {
    return JS_ThrowTypeError(context,
                             "canx.invokeTool requires name and JSON input");
  }

  const char* tool_name = JS_ToCString(context, arguments[0]);
  if (tool_name == nullptr) {
    return JS_EXCEPTION;
  }
  const char* input_json = JS_ToCString(context, arguments[1]);
  if (input_json == nullptr) {
    JS_FreeCString(context, tool_name);
    return JS_EXCEPTION;
  }

  JSValue resolving_functions[2];
  JSValue promise = JS_NewPromiseCapability(context, resolving_functions);
  if (JS_IsException(promise)) {
    JS_FreeCString(context, input_json);
    JS_FreeCString(context, tool_name);
    return promise;
  }

  try {
    const auto request_id = runtime->next_request_id++;
    runtime->pending_calls.emplace(
        request_id,
        PendingToolCall{resolving_functions[0], resolving_functions[1]});
    if (runtime->tool_callback != nullptr) {
      runtime->tool_callback(runtime->callback_context, request_id, tool_name,
                             input_json);
    }
  } catch (const std::exception& exception) {
    JS_FreeValue(context, resolving_functions[0]);
    JS_FreeValue(context, resolving_functions[1]);
    JS_FreeValue(context, promise);
    promise = JS_ThrowInternalError(context, "%s", exception.what());
  } catch (...) {
    JS_FreeValue(context, resolving_functions[0]);
    JS_FreeValue(context, resolving_functions[1]);
    JS_FreeValue(context, promise);
    promise = JS_ThrowInternalError(context, "native tool bridge failure");
  }

  JS_FreeCString(context, input_json);
  JS_FreeCString(context, tool_name);
  return promise;
}

JSModuleDef* load_module(JSContext* context, const char* module_name,
                         void* opaque) {
  auto* runtime = static_cast<canx_quickjs_runtime*>(opaque);
  if (runtime == nullptr || module_name == nullptr) {
    JS_ThrowReferenceError(context, "invalid module request");
    return nullptr;
  }

  const auto iterator = runtime->modules.find(module_name);
  if (iterator == runtime->modules.end()) {
    JS_ThrowReferenceError(context, "module '%s' is not present in plugin package",
                           module_name);
    return nullptr;
  }

  const auto& source = iterator->second;
  JSValue compiled = JS_Eval(
      context, source.data(), source.size(), module_name,
      JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
  if (JS_IsException(compiled)) {
    return nullptr;
  }

  auto* module = static_cast<JSModuleDef*>(JS_VALUE_GET_PTR(compiled));
  JS_FreeValue(context, compiled);
  return module;
}

template <typename Function>
int32_t guard(canx_quickjs_runtime* runtime, Function&& function) noexcept {
  try {
    return function();
  } catch (const std::bad_alloc& exception) {
    if (runtime != nullptr) {
      runtime->last_error = exception.what();
    }
    return allocation_failure;
  } catch (const std::exception& exception) {
    if (runtime != nullptr) {
      runtime->last_error = exception.what();
    }
    return native_exception;
  } catch (...) {
    if (runtime != nullptr) {
      runtime->last_error = "unknown native exception";
    }
    return native_exception;
  }
}

}  // namespace

uint32_t CANX_QUICKJS_CALL canx_quickjs_abi_version(void) {
  return CANX_QUICKJS_ABI_VERSION;
}

int32_t CANX_QUICKJS_CALL canx_quickjs_create(
    const size_t memory_limit, const size_t stack_limit,
    const canx_quickjs_tool_callback tool_callback, void* callback_context,
    canx_quickjs_runtime** out_runtime) {
  if (out_runtime == nullptr || memory_limit == 0 || stack_limit == 0) {
    return invalid_argument;
  }
  *out_runtime = nullptr;

  return guard(nullptr, [&] {
    auto instance = std::make_unique<canx_quickjs_runtime>();
    instance->runtime = JS_NewRuntime();
    if (instance->runtime == nullptr) {
      return allocation_failure;
    }
    JS_SetMemoryLimit(instance->runtime, memory_limit);
    JS_SetMaxStackSize(instance->runtime, stack_limit);
    JS_SetInterruptHandler(instance->runtime, interrupt_handler, instance.get());
    JS_SetModuleLoaderFunc(instance->runtime, nullptr, load_module, instance.get());

    instance->context = JS_NewContext(instance->runtime);
    if (instance->context == nullptr) {
      JS_FreeRuntime(instance->runtime);
      instance->runtime = nullptr;
      return allocation_failure;
    }
    instance->tool_callback = tool_callback;
    instance->callback_context = callback_context;
    JS_SetContextOpaque(instance->context, instance.get());

    JSValue global = JS_GetGlobalObject(instance->context);
    JSValue canx = JS_NewObject(instance->context);
    JS_SetPropertyStr(instance->context, canx, "invokeTool",
                      JS_NewCFunction(instance->context, invoke_tool,
                                      "invokeTool", 2));
    JS_SetPropertyStr(instance->context, global, "canx", canx);
    JS_FreeValue(instance->context, global);

    *out_runtime = instance.release();
    return int32_t{0};
  });
}

void CANX_QUICKJS_CALL canx_quickjs_destroy(canx_quickjs_runtime* runtime) {
  if (runtime == nullptr) {
    return;
  }
  for (const auto& [request_id, call] : runtime->pending_calls) {
    static_cast<void>(request_id);
    JS_FreeValue(runtime->context, call.resolve);
    JS_FreeValue(runtime->context, call.reject);
  }
  runtime->pending_calls.clear();
  for (const auto& evaluation : runtime->pending_module_evaluations) {
    JS_FreeValue(runtime->context, evaluation);
  }
  runtime->pending_module_evaluations.clear();
  JS_FreeContext(runtime->context);
  JS_FreeRuntime(runtime->runtime);
  delete runtime;
}

int32_t CANX_QUICKJS_CALL canx_quickjs_add_module(
    canx_quickjs_runtime* runtime, const char* module_name,
    const char* source) {
  if (runtime == nullptr || module_name == nullptr || module_name[0] == '\0' ||
      source == nullptr) {
    return invalid_argument;
  }
  return guard(runtime, [&] {
    runtime->modules.insert_or_assign(module_name, source);
    return int32_t{0};
  });
}

int32_t CANX_QUICKJS_CALL canx_quickjs_eval(
    canx_quickjs_runtime* runtime, const char* source, const char* filename,
    const int32_t as_module, const uint32_t timeout_ms) {
  if (runtime == nullptr || source == nullptr || filename == nullptr) {
    return invalid_argument;
  }
  return guard(runtime, [&] {
    runtime->last_error.clear();
    JS_UpdateStackTop(runtime->runtime);
    set_deadline(runtime, timeout_ms);
    const auto flags = as_module != 0 ? JS_EVAL_TYPE_MODULE
                                      : JS_EVAL_TYPE_GLOBAL;
    JSValue result =
        JS_Eval(runtime->context, source, std::strlen(source), filename, flags);
    set_deadline(runtime, 0);
    if (JS_IsException(result)) {
      capture_exception(runtime, runtime->context);
      JS_FreeValue(runtime->context, result);
      return javascript_exception;
    }
    if (as_module != 0) {
      runtime->pending_module_evaluations.push_back(result);
      return check_module_evaluations(runtime);
    }
    JS_FreeValue(runtime->context, result);
    return int32_t{0};
  });
}

int32_t CANX_QUICKJS_CALL canx_quickjs_resolve_tool(
    canx_quickjs_runtime* runtime, const uint64_t request_id,
    const char* result_json, const int32_t is_error) {
  if (runtime == nullptr || result_json == nullptr) {
    return invalid_argument;
  }
  return guard(runtime, [&] {
    const auto iterator = runtime->pending_calls.find(request_id);
    if (iterator == runtime->pending_calls.end()) {
      runtime->last_error = "tool request was not found";
      return request_not_found;
    }

    const auto call = iterator->second;
    runtime->pending_calls.erase(iterator);
    JSValue argument = JS_NewString(runtime->context, result_json);
    JSValue function = is_error == 0 ? call.resolve : call.reject;
    JSValue result = JS_Call(runtime->context, function, JS_UNDEFINED, 1,
                             &argument);
    JS_FreeValue(runtime->context, argument);
    JS_FreeValue(runtime->context, call.resolve);
    JS_FreeValue(runtime->context, call.reject);
    if (JS_IsException(result)) {
      capture_exception(runtime, runtime->context);
      JS_FreeValue(runtime->context, result);
      return javascript_exception;
    }
    JS_FreeValue(runtime->context, result);
    return int32_t{0};
  });
}

int32_t CANX_QUICKJS_CALL canx_quickjs_pump_jobs(
    canx_quickjs_runtime* runtime, const int32_t max_jobs,
    const uint32_t timeout_ms, int32_t* out_executed_jobs) {
  if (runtime == nullptr || max_jobs <= 0 || out_executed_jobs == nullptr) {
    return invalid_argument;
  }
  return guard(runtime, [&] {
    *out_executed_jobs = 0;
    JS_UpdateStackTop(runtime->runtime);
    set_deadline(runtime, timeout_ms);
    while (*out_executed_jobs < max_jobs) {
      JSContext* job_context = nullptr;
      const auto result = JS_ExecutePendingJob(runtime->runtime, &job_context);
      if (result < 0) {
        set_deadline(runtime, 0);
        capture_exception(runtime,
                          job_context == nullptr ? runtime->context : job_context);
        return javascript_exception;
      }
      if (result == 0) {
        break;
      }
      ++(*out_executed_jobs);
      const auto module_result = check_module_evaluations(runtime);
      if (module_result != 0) {
        set_deadline(runtime, 0);
        return module_result;
      }
    }
    set_deadline(runtime, 0);
    return check_module_evaluations(runtime);
  });
}

int32_t CANX_QUICKJS_CALL canx_quickjs_get_global_string(
    canx_quickjs_runtime* runtime, const char* property_name, char* output,
    const size_t output_size, size_t* required_size) {
  if (runtime == nullptr || property_name == nullptr || required_size == nullptr) {
    return invalid_argument;
  }
  return guard(runtime, [&] {
    JSValue global = JS_GetGlobalObject(runtime->context);
    JSValue value = JS_GetPropertyStr(runtime->context, global, property_name);
    JS_FreeValue(runtime->context, global);
    if (JS_IsException(value)) {
      capture_exception(runtime, runtime->context);
      JS_FreeValue(runtime->context, value);
      return javascript_exception;
    }

    const char* text = JS_ToCString(runtime->context, value);
    JS_FreeValue(runtime->context, value);
    if (text == nullptr) {
      capture_exception(runtime, runtime->context);
      return javascript_exception;
    }

    const auto length = std::strlen(text);
    *required_size = length + 1;
    if (output == nullptr || output_size < length + 1) {
      JS_FreeCString(runtime->context, text);
      return buffer_too_small;
    }
    std::memcpy(output, text, length + 1);
    JS_FreeCString(runtime->context, text);
    return int32_t{0};
  });
}

const char* CANX_QUICKJS_CALL
canx_quickjs_last_error(canx_quickjs_runtime* runtime) {
  return runtime == nullptr ? "QuickJS runtime is null"
                            : runtime->last_error.c_str();
}
