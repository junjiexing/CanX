#ifndef CANX_WINDOWS_MSVC_COMPAT_SYS_TIME_H
#define CANX_WINDOWS_MSVC_COMPAT_SYS_TIME_H

#include <time.h>

// Windows owns the timeval ABI.  Include Winsock directly so that a later
// include of winsock2.h cannot declare the structure a second time.
#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>

#endif
