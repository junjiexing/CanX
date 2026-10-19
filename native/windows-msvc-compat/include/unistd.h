#ifndef CANX_WINDOWS_MSVC_COMPAT_UNISTD_H
#define CANX_WINDOWS_MSVC_COMPAT_UNISTD_H

// Minimal POSIX compatibility surface required by aria2.  Every mapping
// resolves to the MSVC/UCRT implementation and therefore does not depend on
// the MSYS2 runtime.
#include <direct.h>
#include <fcntl.h>
#include <io.h>
#include <process.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <sys/stat.h>
#include <sys/types.h>

#ifndef STDIN_FILENO
#  define STDIN_FILENO 0
#endif
#ifndef STDOUT_FILENO
#  define STDOUT_FILENO 1
#endif
#ifndef STDERR_FILENO
#  define STDERR_FILENO 2
#endif

#ifndef F_OK
#  define F_OK 0
#endif
#ifndef X_OK
#  define X_OK 1
#endif
#ifndef W_OK
#  define W_OK 2
#endif
#ifndef R_OK
#  define R_OK 4
#endif

#ifndef S_ISREG
#  define S_ISREG(mode) (((mode) & _S_IFMT) == _S_IFREG)
#endif
#ifndef S_ISDIR
#  define S_ISDIR(mode) (((mode) & _S_IFMT) == _S_IFDIR)
#endif

#ifndef _SSIZE_T_DEFINED
#define _SSIZE_T_DEFINED
typedef intptr_t ssize_t;
#endif

static __inline int access(const char* path, int mode)
{
  return _access(path, mode);
}

static __inline int chdir(const char* path) { return _chdir(path); }
static __inline int close(int fd) { return _close(fd); }
static __inline int dup(int fd) { return _dup(fd); }
static __inline int dup2(int source, int target) { return _dup2(source, target); }
static __inline int fileno(FILE* stream) { return _fileno(stream); }
static __inline int fsync(int fd) { return _commit(fd); }
static __inline char* getcwd(char* buffer, int max_length)
{
  return _getcwd(buffer, max_length);
}
static __inline int getpid(void) { return _getpid(); }
static __inline int isatty(int fd) { return _isatty(fd); }
static __inline __int64 lseek(int fd, __int64 offset, int origin)
{
  return _lseeki64(fd, offset, origin);
}
static __inline int open(const char* path, int flags, ...)
{
  int mode = 0;
  if (flags & _O_CREAT) {
    va_list args;
    va_start(args, flags);
    mode = va_arg(args, int);
    va_end(args);
  }
  return _open(path, flags, mode);
}
static __inline ssize_t read(int fd, void* buffer, size_t count)
{
  return _read(fd, buffer, (unsigned int)count);
}
static __inline int rmdir(const char* path) { return _rmdir(path); }
static __inline int unlink(const char* path) { return _unlink(path); }
static __inline ssize_t write(int fd, const void* buffer, size_t count)
{
  return _write(fd, buffer, (unsigned int)count);
}

static __inline int ftruncate(int fd, __int64 length)
{
  return _chsize_s(fd, length) == 0 ? 0 : -1;
}

#endif
