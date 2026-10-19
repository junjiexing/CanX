#include "../include/getopt.h"

#include <cstdio>
#include <cstring>

extern "C" {

char* optarg = nullptr;
int optind = 1;
int opterr = 1;
int optopt = 0;

namespace {

const char* next_short = nullptr;

int missing_argument(const char* optstring, const char* spelling)
{
  if (opterr && optstring[0] != ':') {
    std::fprintf(stderr, "option requires an argument -- %s\n", spelling);
  }
  return optstring[0] == ':' ? ':' : '?';
}

bool is_option(const char* value)
{
  return value != nullptr && value[0] == '-' && value[1] != '\0';
}

void move_next_option_forward(int argc, char* const argv[])
{
  int next = optind + 1;
  while (next < argc && !is_option(argv[next])) {
    ++next;
  }
  if (next == argc) {
    return;
  }

  auto** mutable_argv = const_cast<char**>(argv);
  char* option_arg = mutable_argv[next];
  for (int index = next; index > optind; --index) {
    mutable_argv[index] = mutable_argv[index - 1];
  }
  mutable_argv[optind] = option_arg;
}

int parse_long(int argc, char* const argv[], const char* optstring,
               const option* longopts, int* longindex)
{
  const char* argument = argv[optind] + 2;
  const char* equals = std::strchr(argument, '=');
  const size_t name_length = equals ? static_cast<size_t>(equals - argument)
                                    : std::strlen(argument);
  int match = -1;
  bool ambiguous = false;

  for (int index = 0; longopts[index].name != nullptr; ++index) {
    if (std::strncmp(argument, longopts[index].name, name_length) != 0) {
      continue;
    }
    if (std::strlen(longopts[index].name) == name_length) {
      match = index;
      ambiguous = false;
      break;
    }
    if (match != -1) {
      ambiguous = true;
    } else {
      match = index;
    }
  }

  ++optind;
  if (match == -1 || ambiguous) {
    if (opterr) {
      std::fprintf(stderr, "%s option -- %.*s\n",
                   ambiguous ? "ambiguous" : "unrecognized",
                   static_cast<int>(name_length), argument);
    }
    optopt = 0;
    return '?';
  }

  const option& selected = longopts[match];
  if (longindex) {
    *longindex = match;
  }
  if (equals) {
    if (selected.has_arg == no_argument) {
      if (opterr) {
        std::fprintf(stderr, "option does not allow an argument -- %s\n",
                     selected.name);
      }
      return '?';
    }
    optarg = const_cast<char*>(equals + 1);
  } else if (selected.has_arg == required_argument) {
    if (optind >= argc) {
      return missing_argument(optstring, selected.name);
    }
    optarg = argv[optind++];
  }

  if (selected.flag) {
    *selected.flag = selected.val;
    return 0;
  }
  return selected.val;
}

} // namespace

int getopt_long(int argc, char* const argv[], const char* optstring,
                const struct option* longopts, int* longindex)
{
  optarg = nullptr;

  if (optind <= 0) {
    optind = 1;
    next_short = nullptr;
  }

  if (!next_short || *next_short == '\0') {
    next_short = nullptr;
    while (optind < argc && !is_option(argv[optind])) {
      move_next_option_forward(argc, argv);
      if (!is_option(argv[optind])) {
        return -1;
      }
    }
    if (optind >= argc) {
      return -1;
    }
    if (std::strcmp(argv[optind], "--") == 0) {
      ++optind;
      return -1;
    }
    if (argv[optind][1] == '-' && longopts) {
      return parse_long(argc, argv, optstring, longopts, longindex);
    }
    next_short = argv[optind] + 1;
  }

  const unsigned char current = static_cast<unsigned char>(*next_short++);
  const char* definition = std::strchr(optstring + (optstring[0] == ':'), current);
  if (!definition || current == ':') {
    optopt = current;
    if (*next_short == '\0') {
      ++optind;
      next_short = nullptr;
    }
    if (opterr && optstring[0] != ':') {
      std::fprintf(stderr, "invalid option -- %c\n", current);
    }
    return '?';
  }

  if (definition[1] != ':') {
    if (*next_short == '\0') {
      ++optind;
      next_short = nullptr;
    }
    return current;
  }

  const bool optional = definition[2] == ':';
  if (*next_short != '\0') {
    optarg = const_cast<char*>(next_short);
    ++optind;
  } else if (!optional && optind + 1 < argc) {
    optarg = argv[optind + 1];
    optind += 2;
  } else {
    ++optind;
    next_short = nullptr;
    if (!optional) {
      char spelling[2] = {static_cast<char>(current), '\0'};
      optopt = current;
      return missing_argument(optstring, spelling);
    }
    return current;
  }
  next_short = nullptr;
  return current;
}

int getopt(int argc, char* const argv[], const char* optstring)
{
  return getopt_long(argc, argv, optstring, nullptr, nullptr);
}

} // extern "C"
