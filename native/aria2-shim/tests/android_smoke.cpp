#include "canx_aria2.h"

#include <chrono>
#include <cstdio>
#include <thread>

int main(int argc, char** argv) {
  if (argc != 3) {
    std::fprintf(stderr, "usage: android_smoke URL OUTPUT_DIRECTORY\n");
    return 2;
  }

  if (canx_aria2_library_init() != 0) {
    std::fprintf(stderr, "library init failed: %s\n", canx_aria2_last_error(nullptr));
    return 1;
  }

  canx_aria2_session* session = nullptr;
  const canx_aria2_option session_options[] = {
      {"dir", argv[2]},
      {"check-certificate", "true"},
  };
  auto result = canx_aria2_session_create(session_options, 2, nullptr, nullptr,
                                          &session);
  if (result != 0) {
    std::fprintf(stderr, "session create failed: %s\n", canx_aria2_last_error(nullptr));
    canx_aria2_library_deinit();
    return 1;
  }

  uint64_t gid = 0;
  result = canx_aria2_add_uri(session, argv[1], nullptr, 0, -1, &gid);
  if (result != 0) {
    std::fprintf(stderr, "add failed: %s\n", canx_aria2_last_error(session));
  }

  for (int i = 0; result == 0 && i < 300; ++i) {
    result = canx_aria2_run_once(session);
    if (result < 0) {
      std::fprintf(stderr, "run failed safely: %s\n", canx_aria2_last_error(session));
      break;
    }

    canx_aria2_status status{sizeof(canx_aria2_status)};
    if (canx_aria2_get_status(session, gid, &status) == 0 &&
        (status.status == CANX_ARIA2_DOWNLOAD_COMPLETE ||
         status.status == CANX_ARIA2_DOWNLOAD_ERROR)) {
      std::printf("terminal status=%d error=%d completed=%llu total=%llu\n",
                  status.status, status.error_code,
                  static_cast<unsigned long long>(status.completed_length),
                  static_cast<unsigned long long>(status.total_length));
      break;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
  }

  canx_aria2_shutdown(session, 1);
  canx_aria2_session_destroy(session);
  canx_aria2_library_deinit();
  return result < 0 ? 1 : 0;
}
