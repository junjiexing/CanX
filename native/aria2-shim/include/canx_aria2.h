#ifndef CANX_ARIA2_H
#define CANX_ARIA2_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(CANX_ARIA2_STATIC)
#    define CANX_ARIA2_API
#  elif defined(CANX_ARIA2_BUILDING)
#    define CANX_ARIA2_API __declspec(dllexport)
#  else
#    define CANX_ARIA2_API __declspec(dllimport)
#  endif
#  define CANX_ARIA2_CALL __cdecl
#else
#  define CANX_ARIA2_API __attribute__((visibility("default")))
#  define CANX_ARIA2_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define CANX_ARIA2_ABI_VERSION 6u

typedef struct canx_aria2_session canx_aria2_session;

typedef struct canx_aria2_option {
  const char* key;
  const char* value;
} canx_aria2_option;

typedef enum canx_aria2_download_status {
  CANX_ARIA2_DOWNLOAD_ACTIVE = 0,
  CANX_ARIA2_DOWNLOAD_WAITING = 1,
  CANX_ARIA2_DOWNLOAD_PAUSED = 2,
  CANX_ARIA2_DOWNLOAD_COMPLETE = 3,
  CANX_ARIA2_DOWNLOAD_ERROR = 4,
  CANX_ARIA2_DOWNLOAD_REMOVED = 5
} canx_aria2_download_status;

typedef struct canx_aria2_status {
  uint32_t struct_size;
  int32_t status;
  uint64_t total_length;
  uint64_t completed_length;
  uint64_t uploaded_length;
  uint64_t piece_length;
  int32_t download_speed;
  int32_t upload_speed;
  int32_t num_pieces;
  int32_t connections;
  int32_t error_code;
} canx_aria2_status;

typedef struct canx_aria2_global_status {
  uint32_t struct_size;
  int32_t download_speed;
  int32_t upload_speed;
  int32_t num_active;
  int32_t num_waiting;
  int32_t num_stopped;
} canx_aria2_global_status;

typedef struct canx_aria2_gid_list {
  uint64_t* items;
  size_t count;
} canx_aria2_gid_list;

typedef struct canx_aria2_string {
  char* data;
  size_t length;
} canx_aria2_string;

typedef void(CANX_ARIA2_CALL* canx_aria2_event_callback)(
    void* callback_context,
    int32_t event,
    uint64_t gid);

CANX_ARIA2_API uint32_t CANX_ARIA2_CALL canx_aria2_abi_version(void);
CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_library_init(void);
CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_library_deinit(void);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_session_create(
    const canx_aria2_option* options,
    size_t option_count,
    canx_aria2_event_callback callback,
    void* callback_context,
    canx_aria2_session** out_session);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_session_destroy(
    canx_aria2_session* session);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_add_uri(
    canx_aria2_session* session,
    const char* uri,
    const canx_aria2_option* options,
    size_t option_count,
    int32_t position,
    uint64_t* out_gid);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_add_uris(
    canx_aria2_session* session,
    const char* const* uris,
    size_t uri_count,
    const canx_aria2_option* options,
    size_t option_count,
    int32_t position,
    uint64_t* out_gid);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_add_torrent(
    canx_aria2_session* session,
    const char* torrent_file,
    const canx_aria2_option* options,
    size_t option_count,
    int32_t position,
    uint64_t* out_gid);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_add_metalink(
    canx_aria2_session* session,
    const char* metalink_file,
    const canx_aria2_option* options,
    size_t option_count,
    int32_t position,
    canx_aria2_gid_list* out_gids);

CANX_ARIA2_API void CANX_ARIA2_CALL canx_aria2_gid_list_free(
    canx_aria2_gid_list* list);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_run_once(
    canx_aria2_session* session);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_status(
    canx_aria2_session* session,
    uint64_t gid,
    canx_aria2_status* out_status);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_download_json(
    canx_aria2_session* session,
    uint64_t gid,
    canx_aria2_string* out_json);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_global_options_json(
    canx_aria2_session* session,
    canx_aria2_string* out_json);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_capabilities_json(
    canx_aria2_session* session,
    canx_aria2_string* out_json);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_peers_json(
    canx_aria2_session* session,
    uint64_t gid,
    canx_aria2_string* out_json);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_servers_json(
    canx_aria2_session* session,
    uint64_t gid,
    canx_aria2_string* out_json);

CANX_ARIA2_API void CANX_ARIA2_CALL canx_aria2_string_free(
    canx_aria2_string* value);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_get_global_status(
    canx_aria2_session* session,
    canx_aria2_global_status* out_status);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_pause(
    canx_aria2_session* session,
    uint64_t gid,
    int32_t force);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_unpause(
    canx_aria2_session* session,
    uint64_t gid);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_remove(
    canx_aria2_session* session,
    uint64_t gid,
    int32_t force);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_remove_result(
    canx_aria2_session* session,
    uint64_t gid);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_change_options(
    canx_aria2_session* session,
    uint64_t gid,
    const canx_aria2_option* options,
    size_t option_count);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_change_global_options(
    canx_aria2_session* session,
    const canx_aria2_option* options,
    size_t option_count);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_change_position(
    canx_aria2_session* session,
    uint64_t gid,
    int32_t offset,
    int32_t mode,
    int32_t* out_position);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_prioritize_piece_range(
    canx_aria2_session* session,
    uint64_t gid,
    size_t first_piece,
    size_t last_piece);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_clear_piece_priority(
    canx_aria2_session* session,
    uint64_t gid);

CANX_ARIA2_API int32_t CANX_ARIA2_CALL canx_aria2_shutdown(
    canx_aria2_session* session,
    int32_t force);

CANX_ARIA2_API const char* CANX_ARIA2_CALL canx_aria2_last_error(
    canx_aria2_session* session);

#ifdef __cplusplus
}
#endif

#endif
