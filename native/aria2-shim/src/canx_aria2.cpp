#include "canx_aria2.h"

#include <aria2/aria2.h>

#include <algorithm>
#include <exception>
#include <cstring>
#include <iomanip>
#include <memory>
#include <new>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

struct canx_aria2_session {
  aria2::Session* value = nullptr;
  canx_aria2_event_callback callback = nullptr;
  void* callback_context = nullptr;
  std::string last_error;
};

namespace {

constexpr int32_t invalid_argument = -1000;
constexpr int32_t allocation_failure = -1001;
constexpr int32_t native_exception = -1002;
constexpr int32_t not_found = -1003;
[[maybe_unused]] constexpr int32_t unsupported = -1004;

thread_local std::string global_last_error;

aria2::KeyVals to_key_values(const canx_aria2_option* options,
                             const size_t option_count) {
  aria2::KeyVals result;
  result.reserve(option_count);
  for (size_t index = 0; index < option_count; ++index) {
    if (options[index].key == nullptr || options[index].value == nullptr) {
      throw std::invalid_argument("option key and value must not be null");
    }
    result.emplace_back(options[index].key, options[index].value);
  }
  return result;
}

void write_json_string(std::ostringstream& output, const std::string_view value) {
  output << '"';
  for (const auto character : value) {
    const auto byte = static_cast<unsigned char>(character);
    switch (character) {
      case '"':
        output << "\\\"";
        break;
      case '\\':
        output << "\\\\";
        break;
      case '\b':
        output << "\\b";
        break;
      case '\f':
        output << "\\f";
        break;
      case '\n':
        output << "\\n";
        break;
      case '\r':
        output << "\\r";
        break;
      case '\t':
        output << "\\t";
        break;
      default:
        if (byte < 0x20) {
          output << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                 << static_cast<int>(byte) << std::dec << std::setfill(' ');
        } else {
          output << character;
        }
    }
  }
  output << '"';
}

void write_hex(std::ostringstream& output, const std::string_view bytes) {
  static constexpr char digits[] = "0123456789abcdef";
  output << '"';
  for (const auto character : bytes) {
    const auto byte = static_cast<unsigned char>(character);
    output << digits[byte >> 4] << digits[byte & 0x0f];
  }
  output << '"';
}

void write_gid(std::ostringstream& output, const aria2::A2Gid gid) {
  write_json_string(output, aria2::gidToHex(gid));
}

void write_key_values(std::ostringstream& output,
                      const aria2::KeyVals& values) {
  output << '{';
  bool first = true;
  for (const auto& [key, value] : values) {
    if (!first) {
      output << ',';
    }
    first = false;
    write_json_string(output, key);
    output << ':';
    write_json_string(output, value);
  }
  output << '}';
}

void set_string_result(const std::string& value, canx_aria2_string* result) {
  auto data = std::make_unique<char[]>(value.size() + 1);
  std::memcpy(data.get(), value.data(), value.size());
  data[value.size()] = '\0';
  result->data = data.release();
  result->length = value.size();
}

using DownloadHandle =
    std::unique_ptr<aria2::DownloadHandle,
                    decltype(&aria2::deleteDownloadHandle)>;

DownloadHandle get_download_handle(canx_aria2_session* session,
                                   const uint64_t gid) {
  DownloadHandle handle(aria2::getDownloadHandle(session->value, gid),
                        aria2::deleteDownloadHandle);
  if (!handle) {
    throw std::out_of_range("download GID was not found");
  }
  return handle;
}

void set_error(canx_aria2_session* session, const std::string& message) {
  if (session != nullptr) {
    session->last_error = message;
  } else {
    global_last_error = message;
  }
}

int on_download_event(aria2::Session*, const aria2::DownloadEvent event,
                      const aria2::A2Gid gid, void* user_data) noexcept {
  auto* session = static_cast<canx_aria2_session*>(user_data);
  if (session != nullptr && session->callback != nullptr) {
    session->callback(session->callback_context, static_cast<int32_t>(event),
                      gid);
  }
  return 0;
}

template <typename Function>
int32_t guard(canx_aria2_session* session, Function&& function) noexcept {
  try {
    return function();
  } catch (const std::bad_alloc& exception) {
    set_error(session, exception.what());
    return allocation_failure;
  } catch (const std::out_of_range& exception) {
    set_error(session, exception.what());
    return not_found;
  } catch (const std::exception& exception) {
    set_error(session, exception.what());
    return native_exception;
  } catch (...) {
    set_error(session, "unknown native exception");
    return native_exception;
  }
}

}  // namespace

uint32_t CANX_ARIA2_CALL canx_aria2_abi_version(void) {
  return CANX_ARIA2_ABI_VERSION;
}

int32_t CANX_ARIA2_CALL canx_aria2_library_init(void) {
  return guard(nullptr, [] { return aria2::libraryInit(); });
}

int32_t CANX_ARIA2_CALL canx_aria2_library_deinit(void) {
  return guard(nullptr, [] { return aria2::libraryDeinit(); });
}

int32_t CANX_ARIA2_CALL canx_aria2_session_create(
    const canx_aria2_option* options, const size_t option_count,
    const canx_aria2_event_callback callback, void* callback_context,
    canx_aria2_session** out_session) {
  if (out_session == nullptr || (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  *out_session = nullptr;

  return guard(nullptr, [&] {
    auto wrapper = std::make_unique<canx_aria2_session>();
    wrapper->callback = callback;
    wrapper->callback_context = callback_context;

    aria2::SessionConfig config;
    config.keepRunning = true;
    config.useSignalHandler = false;
    config.downloadEventCallback = on_download_event;
    config.userData = wrapper.get();

    const auto key_values = to_key_values(options, option_count);
    wrapper->value = aria2::sessionNew(key_values, config);
    if (wrapper->value == nullptr) {
      set_error(nullptr, "aria2::sessionNew returned null");
      return native_exception;
    }

    *out_session = wrapper.release();
    return int32_t{0};
  });
}

int32_t CANX_ARIA2_CALL
canx_aria2_session_destroy(canx_aria2_session* session) {
  if (session == nullptr) {
    return invalid_argument;
  }
  return guard(session, [&] {
    const auto result = aria2::sessionFinal(session->value);
    session->value = nullptr;
    delete session;
    return result;
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_add_uri(
    canx_aria2_session* session, const char* uri,
    const canx_aria2_option* options, const size_t option_count,
    const int32_t position, uint64_t* out_gid) {
  if (session == nullptr || uri == nullptr || out_gid == nullptr ||
      (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    aria2::A2Gid gid = 0;
    const std::vector<std::string> uris{uri};
    const auto result = aria2::addUri(session->value, &gid, uris,
                                      to_key_values(options, option_count),
                                      position);
    *out_gid = gid;
    return result;
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_add_uris(
    canx_aria2_session* session, const char* const* uris,
    const size_t uri_count, const canx_aria2_option* options,
    const size_t option_count, const int32_t position, uint64_t* out_gid) {
  if (session == nullptr || uris == nullptr || uri_count == 0 ||
      out_gid == nullptr || (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    std::vector<std::string> native_uris;
    native_uris.reserve(uri_count);
    for (size_t index = 0; index < uri_count; ++index) {
      if (uris[index] == nullptr) {
        throw std::invalid_argument("URI must not be null");
      }
      native_uris.emplace_back(uris[index]);
    }

    aria2::A2Gid gid = 0;
    const auto result = aria2::addUri(session->value, &gid, native_uris,
                                      to_key_values(options, option_count),
                                      position);
    *out_gid = gid;
    return result;
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_add_torrent(
    canx_aria2_session* session, const char* torrent_file,
    const canx_aria2_option* options, const size_t option_count,
    const int32_t position, uint64_t* out_gid) {
  if (session == nullptr || torrent_file == nullptr || out_gid == nullptr ||
      (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    aria2::A2Gid gid = 0;
    const auto result = aria2::addTorrent(session->value, &gid, torrent_file,
                                          to_key_values(options, option_count),
                                          position);
    *out_gid = gid;
    return result;
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_add_metalink(
    canx_aria2_session* session, const char* metalink_file,
    const canx_aria2_option* options, const size_t option_count,
    const int32_t position, canx_aria2_gid_list* out_gids) {
  if (session == nullptr || metalink_file == nullptr || out_gids == nullptr ||
      (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  out_gids->items = nullptr;
  out_gids->count = 0;

  return guard(session, [&] {
    std::vector<aria2::A2Gid> gids;
    const auto result = aria2::addMetalink(session->value, &gids,
                                           metalink_file,
                                           to_key_values(options, option_count),
                                           position);
    if (result < 0 || gids.empty()) {
      return result;
    }

    auto items = std::make_unique<uint64_t[]>(gids.size());
    std::copy(gids.begin(), gids.end(), items.get());
    out_gids->items = items.release();
    out_gids->count = gids.size();
    return result;
  });
}

void CANX_ARIA2_CALL canx_aria2_gid_list_free(canx_aria2_gid_list* list) {
  if (list == nullptr) {
    return;
  }
  delete[] list->items;
  list->items = nullptr;
  list->count = 0;
}

int32_t CANX_ARIA2_CALL canx_aria2_run_once(canx_aria2_session* session) {
  if (session == nullptr) {
    return invalid_argument;
  }
  return guard(session,
               [&] { return aria2::run(session->value, aria2::RUN_ONCE); });
}

int32_t CANX_ARIA2_CALL canx_aria2_get_status(
    canx_aria2_session* session, const uint64_t gid,
    canx_aria2_status* out_status) {
  if (session == nullptr || out_status == nullptr ||
      out_status->struct_size < sizeof(canx_aria2_status)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    auto handle = get_download_handle(session, gid);

    canx_aria2_status status{};
    status.struct_size = sizeof(canx_aria2_status);
    status.status = static_cast<int32_t>(handle->getStatus());
    status.total_length = static_cast<uint64_t>(handle->getTotalLength());
    status.completed_length =
        static_cast<uint64_t>(handle->getCompletedLength());
    status.uploaded_length =
        static_cast<uint64_t>(handle->getUploadLength());
    status.piece_length = static_cast<uint64_t>(handle->getPieceLength());
    status.download_speed = handle->getDownloadSpeed();
    status.upload_speed = handle->getUploadSpeed();
    status.num_pieces = handle->getNumPieces();
    status.connections = handle->getConnections();
    status.error_code = handle->getErrorCode();
    *out_status = status;
    return int32_t{0};
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_get_download_json(
    canx_aria2_session* session, const uint64_t gid,
    canx_aria2_string* out_json) {
  if (session == nullptr || out_json == nullptr) {
    return invalid_argument;
  }
  out_json->data = nullptr;
  out_json->length = 0;

  try {
    auto handle = get_download_handle(session, gid);
#ifdef CANX_ARIA2_EXTENDED_API
    const auto bittorrent_status =
        aria2::getBitTorrentStatus(session->value, gid);
#endif
    std::ostringstream output;
    output << "{\"gid\":";
    write_gid(output, gid);
    output << ",\"status\":" << static_cast<int32_t>(handle->getStatus())
           << ",\"totalLength\":" << handle->getTotalLength()
           << ",\"completedLength\":" << handle->getCompletedLength()
           << ",\"uploadedLength\":" << handle->getUploadLength()
           << ",\"downloadSpeed\":" << handle->getDownloadSpeed()
           << ",\"uploadSpeed\":" << handle->getUploadSpeed()
           << ",\"pieceLength\":" << handle->getPieceLength()
           << ",\"numPieces\":" << handle->getNumPieces()
           << ",\"connections\":" << handle->getConnections()
#ifdef CANX_ARIA2_EXTENDED_API
           << ",\"seeder\":"
           << (bittorrent_status.seeder ? "true" : "false")
           << ",\"numSeeders\":" << bittorrent_status.numSeeders
#else
           << ",\"seeder\":false,\"numSeeders\":0"
#endif
           << ",\"errorCode\":" << handle->getErrorCode()
           << ",\"dir\":";
    write_json_string(output, handle->getDir());
    const auto info_hash = handle->getInfoHash();
    output << ",\"infoHash\":";
    write_hex(output, info_hash);
    output << ",\"bitfield\":";
    write_hex(output, handle->getBitfield());

    output << ",\"followedBy\":[";
    bool first = true;
    for (const auto followed_gid : handle->getFollowedBy()) {
      if (!first) {
        output << ',';
      }
      first = false;
      write_gid(output, followed_gid);
    }
    output << "],\"following\":";
    write_gid(output, handle->getFollowing());
    output << ",\"belongsTo\":";
    write_gid(output, handle->getBelongsTo());

    output << ",\"files\":[";
    first = true;
    for (const auto& file : handle->getFiles()) {
      if (!first) {
        output << ',';
      }
      first = false;
      output << "{\"index\":" << file.index << ",\"path\":";
      write_json_string(output, file.path);
      output << ",\"length\":" << file.length
             << ",\"completedLength\":" << file.completedLength
             << ",\"selected\":" << (file.selected ? "true" : "false")
             << ",\"uris\":[";
      bool first_uri = true;
      for (const auto& uri : file.uris) {
        if (!first_uri) {
          output << ',';
        }
        first_uri = false;
        output << "{\"uri\":";
        write_json_string(output, uri.uri);
        output << ",\"status\":" << static_cast<int32_t>(uri.status) << '}';
      }
      output << "]}";
    }
    output << "],\"options\":";
    write_key_values(output, handle->getOptions());

    output << ",\"torrent\":{\"name\":";
    if (info_hash.empty()) {
      output << "\"\",\"comment\":\"\",\"creationDate\":0,\"mode\":0,"
                "\"announceList\":[]}";
    } else {
      const auto torrent = handle->getBtMetaInfo();
      write_json_string(output, torrent.name);
      output << ",\"comment\":";
      write_json_string(output, torrent.comment);
      output << ",\"creationDate\":"
             << static_cast<int64_t>(torrent.creationDate)
             << ",\"mode\":" << static_cast<int32_t>(torrent.mode)
             << ",\"announceList\":[";
      bool first_tier = true;
      for (const auto& tier : torrent.announceList) {
        if (!first_tier) {
          output << ',';
        }
        first_tier = false;
        output << '[';
        bool first_tracker = true;
        for (const auto& tracker : tier) {
          if (!first_tracker) {
            output << ',';
          }
          first_tracker = false;
          write_json_string(output, tracker);
        }
        output << ']';
      }
      output << "]}";
    }
    output << '}';
    set_string_result(output.str(), out_json);
    return int32_t{0};
  } catch (const std::out_of_range& exception) {
    set_error(session, exception.what());
    return not_found;
  } catch (const std::bad_alloc& exception) {
    set_error(session, exception.what());
    return allocation_failure;
  } catch (const std::exception& exception) {
    set_error(session, exception.what());
    return native_exception;
  } catch (...) {
    set_error(session, "unknown native exception");
    return native_exception;
  }
}

int32_t CANX_ARIA2_CALL canx_aria2_get_global_options_json(
    canx_aria2_session* session, canx_aria2_string* out_json) {
  if (session == nullptr || out_json == nullptr) {
    return invalid_argument;
  }
  out_json->data = nullptr;
  out_json->length = 0;
  return guard(session, [&] {
    std::ostringstream output;
    write_key_values(output, aria2::getGlobalOptions(session->value));
    set_string_result(output.str(), out_json);
    return int32_t{0};
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_get_capabilities_json(
    canx_aria2_session* session, canx_aria2_string* out_json) {
  if (session == nullptr || out_json == nullptr) {
    return invalid_argument;
  }
  out_json->data = nullptr;
  out_json->length = 0;
  return guard(session, [&] {
    std::ostringstream output;
    output << "{\"abiVersion\":" << CANX_ARIA2_ABI_VERSION
           << ",\"extendedApi\":";
#ifdef CANX_ARIA2_EXTENDED_API
    output << "true,\"features\":[";
    bool first = true;
    for (const auto& feature : aria2::getSupportedFeatures()) {
      if (!first) {
        output << ',';
      }
      first = false;
      write_json_string(output, feature);
    }

    if (!first) {
      output << ',';
    }
    write_json_string(output, "runtime-piece-priority");

    output << "],\"options\":[";
    first = true;
    for (const auto& option : aria2::getOptionMetadata()) {
      if (!first) {
        output << ',';
      }
      first = false;
      output << "{\"name\":";
      write_json_string(output, option.name);
      output << ",\"description\":";
      write_json_string(output, option.description);
      output << ",\"defaultValue\":";
      write_json_string(output, option.defaultValue);
      output << ",\"possibleValues\":";
      write_json_string(output, option.possibleValues);
      output << ",\"shortName\":";
      if (option.shortName == 0) {
        output << "\"\"";
      } else {
        write_json_string(output, std::string(1, option.shortName));
      }
      output << ",\"argumentType\":" << option.argumentType
             << ",\"hidden\":" << (option.hidden ? "true" : "false")
             << ",\"initial\":"
             << (option.initialOption ? "true" : "false")
             << ",\"changeActive\":"
             << (option.changeOption ? "true" : "false")
             << ",\"changeReserved\":"
             << (option.changeOptionForReserved ? "true" : "false")
             << ",\"changeGlobal\":"
             << (option.changeGlobalOption ? "true" : "false")
             << ",\"cumulative\":"
             << (option.cumulative ? "true" : "false") << '}';
    }
    output << "]}";
#else
    output << "false,\"features\":[],\"options\":[]}";
#endif
    set_string_result(output.str(), out_json);
    return int32_t{0};
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_get_peers_json(
    canx_aria2_session* session, const uint64_t gid,
    canx_aria2_string* out_json) {
  if (session == nullptr || out_json == nullptr) {
    return invalid_argument;
  }
  out_json->data = nullptr;
  out_json->length = 0;
#ifndef CANX_ARIA2_EXTENDED_API
  (void)gid;
  set_error(session, "peer details require the CanX aria2 extended API");
  return unsupported;
#else
  return guard(session, [&] {
    std::ostringstream output;
    output << '[';
    bool first = true;
    for (const auto& peer : aria2::getPeers(session->value, gid)) {
      if (!first) {
        output << ',';
      }
      first = false;
      output << "{\"peerId\":";
      write_hex(output, peer.peerId);
      output << ",\"ip\":";
      write_json_string(output, peer.ip);
      output << ",\"port\":" << peer.port << ",\"bitfield\":";
      write_hex(output, peer.bitfield);
      output << ",\"downloadSpeed\":" << peer.downloadSpeed
             << ",\"uploadSpeed\":" << peer.uploadSpeed
             << ",\"amChoking\":" << (peer.amChoking ? "true" : "false")
             << ",\"peerChoking\":"
             << (peer.peerChoking ? "true" : "false")
             << ",\"seeder\":" << (peer.seeder ? "true" : "false") << '}';
    }
    output << ']';
    set_string_result(output.str(), out_json);
    return int32_t{0};
  });
#endif
}

int32_t CANX_ARIA2_CALL canx_aria2_get_servers_json(
    canx_aria2_session* session, const uint64_t gid,
    canx_aria2_string* out_json) {
  if (session == nullptr || out_json == nullptr) {
    return invalid_argument;
  }
  out_json->data = nullptr;
  out_json->length = 0;
#ifndef CANX_ARIA2_EXTENDED_API
  (void)gid;
  set_error(session, "server details require the CanX aria2 extended API");
  return unsupported;
#else
  return guard(session, [&] {
    std::ostringstream output;
    output << '[';
    bool first = true;
    for (const auto& server : aria2::getServers(session->value, gid)) {
      if (!first) {
        output << ',';
      }
      first = false;
      output << "{\"fileIndex\":" << server.fileIndex << ",\"uri\":";
      write_json_string(output, server.uri);
      output << ",\"currentUri\":";
      write_json_string(output, server.currentUri);
      output << ",\"downloadSpeed\":" << server.downloadSpeed << '}';
    }
    output << ']';
    set_string_result(output.str(), out_json);
    return int32_t{0};
  });
#endif
}

void CANX_ARIA2_CALL canx_aria2_string_free(canx_aria2_string* value) {
  if (value == nullptr) {
    return;
  }
  delete[] value->data;
  value->data = nullptr;
  value->length = 0;
}

int32_t CANX_ARIA2_CALL canx_aria2_get_global_status(
    canx_aria2_session* session, canx_aria2_global_status* out_status) {
  if (session == nullptr || out_status == nullptr ||
      out_status->struct_size < sizeof(canx_aria2_global_status)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    const auto value = aria2::getGlobalStat(session->value);
    canx_aria2_global_status status{};
    status.struct_size = sizeof(canx_aria2_global_status);
    status.download_speed = value.downloadSpeed;
    status.upload_speed = value.uploadSpeed;
    status.num_active = value.numActive;
    status.num_waiting = value.numWaiting;
    status.num_stopped = value.numStopped;
    *out_status = status;
    return int32_t{0};
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_pause(canx_aria2_session* session,
                                         const uint64_t gid,
                                         const int32_t force) {
  if (session == nullptr) {
    return invalid_argument;
  }
  return guard(session, [&] {
    return aria2::pauseDownload(session->value, gid, force != 0);
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_unpause(canx_aria2_session* session,
                                           const uint64_t gid) {
  if (session == nullptr) {
    return invalid_argument;
  }
  return guard(
      session, [&] { return aria2::unpauseDownload(session->value, gid); });
}

int32_t CANX_ARIA2_CALL canx_aria2_remove(canx_aria2_session* session,
                                          const uint64_t gid,
                                          const int32_t force) {
  if (session == nullptr) {
    return invalid_argument;
  }
  return guard(session, [&] {
    return aria2::removeDownload(session->value, gid, force != 0);
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_remove_result(
    canx_aria2_session* session, const uint64_t gid) {
  if (session == nullptr) {
    return invalid_argument;
  }
#ifndef CANX_ARIA2_EXTENDED_API
  (void)gid;
  set_error(session, "stopped result removal requires the CanX aria2 extended API");
  return unsupported;
#else
  return guard(session, [&] {
    return aria2::removeDownloadResult(session->value, gid);
  });
#endif
}

int32_t CANX_ARIA2_CALL canx_aria2_change_options(
    canx_aria2_session* session, const uint64_t gid,
    const canx_aria2_option* options, const size_t option_count) {
  if (session == nullptr || (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    return aria2::changeOption(session->value, gid,
                               to_key_values(options, option_count));
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_change_global_options(
    canx_aria2_session* session, const canx_aria2_option* options,
    const size_t option_count) {
  if (session == nullptr || (option_count > 0 && options == nullptr)) {
    return invalid_argument;
  }
  return guard(session, [&] {
    return aria2::changeGlobalOption(session->value,
                                     to_key_values(options, option_count));
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_change_position(
    canx_aria2_session* session, const uint64_t gid, const int32_t offset,
    const int32_t mode, int32_t* out_position) {
  if (session == nullptr || out_position == nullptr || mode < 0 || mode > 2) {
    return invalid_argument;
  }
  return guard(session, [&] {
    const auto position = aria2::changePosition(
        session->value, gid, offset, static_cast<aria2::OffsetMode>(mode));
    if (position < 0) {
      return position;
    }
    *out_position = position;
    return int32_t{0};
  });
}

int32_t CANX_ARIA2_CALL canx_aria2_prioritize_piece_range(
    canx_aria2_session* session, const uint64_t gid,
    const size_t first_piece, const size_t last_piece) {
  if (session == nullptr || first_piece > last_piece) {
    return invalid_argument;
  }
#ifndef CANX_ARIA2_EXTENDED_API
  (void)gid;
  (void)first_piece;
  (void)last_piece;
  set_error(session,
            "runtime piece priority requires the CanX aria2 extended API");
  return unsupported;
#else
  return guard(session, [&] {
    return aria2::prioritizePieceRange(session->value, gid, first_piece,
                                       last_piece);
  });
#endif
}

int32_t CANX_ARIA2_CALL canx_aria2_clear_piece_priority(
    canx_aria2_session* session, const uint64_t gid) {
  if (session == nullptr) {
    return invalid_argument;
  }
#ifndef CANX_ARIA2_EXTENDED_API
  (void)gid;
  set_error(session,
            "runtime piece priority requires the CanX aria2 extended API");
  return unsupported;
#else
  return guard(session,
               [&] { return aria2::clearPiecePriority(session->value, gid); });
#endif
}

int32_t CANX_ARIA2_CALL canx_aria2_shutdown(canx_aria2_session* session,
                                            const int32_t force) {
  if (session == nullptr) {
    return invalid_argument;
  }
  return guard(session,
               [&] { return aria2::shutdown(session->value, force != 0); });
}

const char* CANX_ARIA2_CALL
canx_aria2_last_error(canx_aria2_session* session) {
  return session == nullptr ? global_last_error.c_str()
                            : session->last_error.c_str();
}
