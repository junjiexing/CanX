#ifndef CANX_WINDOWS_MSVC_COMPAT_SYS_TYPES_H
#define CANX_WINDOWS_MSVC_COMPAT_SYS_TYPES_H

#include_next <sys/types.h>
#include <stdint.h>

#ifndef _SSIZE_T_DEFINED
#  define _SSIZE_T_DEFINED
typedef intptr_t ssize_t;
#endif

#endif
