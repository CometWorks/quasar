#define _GNU_SOURCE

#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

static int parse_int(const char *text, int min, int max, int *value)
{
    char *end = NULL;
    long parsed;

    errno = 0;
    parsed = strtol(text, &end, 10);
    if (errno != 0 || end == text || *end != '\0' || parsed < min || parsed > max)
        return 0;

    *value = (int)parsed;
    return 1;
}

static int is_allowed_nice(int nice)
{
    return nice == -10 || nice == -5 || nice == 0 || nice == 5 || nice == 10;
}

static int is_allowed_magnetar_executable(const char *path)
{
    static const char *names[] = {
        "MagnetarInterim",
        "MagnetarInterim.exe",
        "MagnetarLegacy.exe",
    };
    const char *name = strrchr(path, '/');
    name = name == NULL ? path : name + 1;

    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); i++)
    {
        if (strcmp(name, names[i]) == 0)
            return 1;
    }

    return 0;
}

static int validate_target(pid_t pid)
{
    char path[64];
    char cmdline[8192];
    struct stat status_stat;
    FILE *file;
    size_t bytes_read;
    uid_t caller_uid = getuid();

    snprintf(path, sizeof(path), "/proc/%d", pid);
    if (stat(path, &status_stat) != 0)
    {
        perror("stat target process");
        return 0;
    }

    if (status_stat.st_uid != caller_uid)
    {
        fprintf(stderr, "target process is not owned by the caller\n");
        return 0;
    }

    snprintf(path, sizeof(path), "/proc/%d/cmdline", pid);
    file = fopen(path, "rb");
    if (file == NULL)
    {
        perror("open target cmdline");
        return 0;
    }

    bytes_read = fread(cmdline, 1, sizeof(cmdline) - 1, file);
    fclose(file);
    cmdline[bytes_read] = '\0';

    if (bytes_read == 0 || !is_allowed_magnetar_executable(cmdline))
    {
        fprintf(stderr, "target executable is not a supported Magnetar launcher\n");
        return 0;
    }

    return 1;
}

int main(int argc, char **argv)
{
    int nice;
    int pid_value;
    pid_t pid;

    if (argc != 3)
    {
        fprintf(stderr, "usage: quasar-renice <nice> <pid>\n");
        return 2;
    }

    if (!parse_int(argv[1], -20, 19, &nice) || !is_allowed_nice(nice))
    {
        fprintf(stderr, "nice value is not allowed\n");
        return 2;
    }

    if (!parse_int(argv[2], 2, INT_MAX, &pid_value))
    {
        fprintf(stderr, "invalid pid\n");
        return 2;
    }
    pid = (pid_t)pid_value;

    if (geteuid() != 0)
    {
        fprintf(stderr, "quasar-renice must be installed setuid root\n");
        return 1;
    }

    if (!validate_target(pid))
        return 1;

    if (setpriority(PRIO_PROCESS, pid, nice) != 0)
    {
        perror("setpriority");
        return 1;
    }

    return 0;
}
